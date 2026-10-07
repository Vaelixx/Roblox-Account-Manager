using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public class AccountStore
{
    private static string StorePath => Paths.InData("accounts.dat");
    private static string BackupPath => Paths.InData("accounts.bak");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // Serialises disk writes: Save() runs on the UI thread and from background callbacks (a cookie
    // rotated during a launch, the local API) — two interleaved saves could corrupt the store.
    private static readonly object _saveLock = new();
    private static long _nextSaveId;
    private static long _lastWrittenSaveId;

    public ObservableCollection<Account> Accounts { get; } = new();

    /// <summary>null = no master password (DPAPI mode). Non-null = current master password.</summary>
    public string? MasterPassword { get; private set; }

    /// <summary>Debug demo mode: accounts live in memory only and are never written.</summary>
    internal bool IsReadOnly { get; set; }

    /// <summary>
    /// Set once <see cref="Load"/> has read the file (or found there is none). Until then the list is
    /// empty only because nothing has been read yet, and saving it would replace the real file with
    /// an empty one — what "Update now" over the unlock prompt did in 2.0.
    /// </summary>
    private volatile bool _opened;

    /// <summary>Raised after a successful save (the Overview refreshes its health panel from it).</summary>
    public event Action? Saved;

    public bool IsPasswordProtected
    {
        get
        {
            if (!File.Exists(StorePath)) return false;
            try { return Crypto.IsPasswordProtected(File.ReadAllBytes(StorePath)); }
            catch { return false; }
        }
    }

    public bool StoreExists => File.Exists(StorePath);

    // ---------------------------------------------------------------
    //  Persistence
    // ---------------------------------------------------------------

    /// <summary>Loads accounts. Returns false if a (wrong/missing) password or a foreign DPAPI key blocked decryption.</summary>
    public bool Load(string? password)
    {
        _opened = false;
        Accounts.Clear();
        if (!File.Exists(StorePath)) { MasterPassword = password; _opened = true; return true; }

        try
        {
            byte[] data = File.ReadAllBytes(StorePath);
            byte[] plain = Crypto.Decrypt(data, password);
            MasterPassword = Crypto.IsPasswordProtected(data) ? password : null;

            string json = Encoding.UTF8.GetString(plain);
            var list = JsonSerializer.Deserialize<List<Persisted>>(json) ?? new();
            int unreadable = 0;
            foreach (var p in list)
            {
                var acc = p.ToAccount();
                if (acc.UnreadableCookie != null) unreadable++;
                Accounts.Add(acc);
            }
            if (unreadable > 0)
                DiagnosticsService.Warn("store", $"{unreadable} account cookie(s) could not be decrypted for this Windows user; they are kept untouched");
            _opened = true;
            return true;
        }
        catch (Exception ex)
        {
            Accounts.Clear();
            DiagnosticsService.Warn("store", "Account store could not be opened", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------
    //  Recovery from the 2.0 update accident
    // ---------------------------------------------------------------

    /// <summary>
    /// The trace "Update now" over 2.0's unlock prompt left behind: it saved the never-opened, empty
    /// store, so accounts.dat became an empty file without a master password while the real,
    /// password-protected file survived only as accounts.bak.
    /// </summary>
    public bool LooksEmptiedByUpdate
    {
        get
        {
            if (!_opened || MasterPassword != null || Accounts.Count > 0) return false;
            try { return File.Exists(BackupPath) && Crypto.IsPasswordProtected(File.ReadAllBytes(BackupPath)); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Makes the password-protected accounts.bak the account file again and opens it. False when the
    /// password does not open the backup. The empty file is kept aside, never deleted.
    /// </summary>
    public bool TryRestoreBackup(string password)
    {
        try
        {
            Crypto.Decrypt(File.ReadAllBytes(BackupPath), password);   // throws on a wrong password
        }
        catch { return false; }

        try
        {
            lock (_saveLock)
            {
                if (File.Exists(StorePath))
                    File.Move(StorePath, Paths.InData($"accounts.empty-{DateTime.Now:yyyyMMdd-HHmmss}.dat"));
                File.Copy(BackupPath, StorePath);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("store", "The account backup could not be restored", ex);
            return false;
        }
        return Load(password);
    }

    /// <summary>Moves accounts.bak out of the way, unchanged, so no later save can overwrite it. Returns its new path.</summary>
    public string? KeepBackupAside()
    {
        try
        {
            string kept = Paths.InData($"accounts.bak-{DateTime.Now:yyyyMMdd-HHmmss}.kept");
            lock (_saveLock) File.Move(BackupPath, kept);
            return kept;
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("store", "The account backup could not be moved aside", ex);
            return null;
        }
    }

    public void Save()
    {
        if (IsReadOnly) return;
        if (!_opened)
        {
            DiagnosticsService.Warn("store", "Not saved: the account store has not been opened yet");
            return;
        }

        long saveId = Interlocked.Increment(ref _nextSaveId);

        try
        {
            // Each cookie is DPAPI-wrapped on its own, so it is never plain text — not even inside the
            // (already encrypted) store JSON. Keep the slow crypto outside the disk lock.
            var dtos = SnapshotForSave();
            string json = JsonSerializer.Serialize(dtos, JsonOpts);
            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = MasterPassword is { Length: > 0 }
                ? Crypto.EncryptPassword(plain, MasterPassword)
                : Crypto.EncryptDpapi(plain);

            lock (_saveLock)
            {
                if (saveId < _lastWrittenSaveId) return;

                Directory.CreateDirectory(Paths.DataDir);
                if (File.Exists(StorePath))
                    File.Copy(StorePath, BackupPath, overwrite: true);

                // Atomic write: stage, then swap, so a crash mid-write can never truncate accounts.dat.
                string tmp = StorePath + ".tmp";
                File.WriteAllBytes(tmp, encrypted);
                if (File.Exists(StorePath))
                    File.Replace(tmp, StorePath, null);
                else
                    File.Move(tmp, StorePath);

                _lastWrittenSaveId = saveId;
            }
            try { Saved?.Invoke(); } catch { }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("store", "Accounts could not be saved", ex);
        }
    }

    private List<Persisted> SnapshotForSave()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
            return dispatcher.Invoke(() => Accounts.ToArray().Select(Persisted.FromAccount).ToList());

        return Accounts.ToArray().Select(Persisted.FromAccount).ToList();
    }

    /// <summary>On-disk shape. Cookie and 2FA secret are stored DPAPI-protected (enc1:…), never raw.</summary>
    private class Persisted
    {
        public string Cookie { get; set; } = "";
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Group { get; set; } = "Default";
        public string BrowserTrackerId { get; set; } = "";
        public Dictionary<string, string> Fields { get; set; } = new();
        public DateTime LastUse { get; set; }
        public string Alias { get; set; } = "";
        public string Description { get; set; } = "";
        public string TotpSecret { get; set; } = "";
        public string ProxyUrl { get; set; } = "";
        public string FFlags { get; set; } = "";
        public bool AutoRejoin { get; set; }
        public bool IsFavorite { get; set; }
        public string Color { get; set; } = "";
        public DateTime? AddedUtc { get; set; }
        public DateTime? CookieUpdatedUtc { get; set; }
        public DateTime? LastValidatedUtc { get; set; }
        public DateTime? CookieRejectedUtc { get; set; }

        public static Persisted FromAccount(Account a) => new()
        {
            // A value this Windows user cannot decrypt is written back exactly as it was read, so
            // opening the store on the wrong account never destroys the real cookie.
            Cookie = a.UnreadableCookie ?? Crypto.ProtectString(a.Cookie),
            TotpSecret = a.UnreadableTotp ?? Crypto.ProtectString(a.TotpSecret),
            UserId = a.UserId,
            Username = a.Username,
            DisplayName = a.DisplayName,
            Group = a.Group,
            BrowserTrackerId = a.BrowserTrackerId,
            Fields = a.Fields,
            LastUse = a.LastUse,
            Alias = a.Alias,
            Description = a.Description,
            ProxyUrl = a.ProxyUrl,
            FFlags = a.FFlags,
            AutoRejoin = a.AutoRejoin,
            IsFavorite = a.IsFavorite,
            Color = a.Color,
            AddedUtc = a.AddedUtc,
            CookieUpdatedUtc = a.CookieUpdatedUtc,
            LastValidatedUtc = a.LastValidatedUtc,
            CookieRejectedUtc = a.CookieRejectedUtc,
        };

        public Account ToAccount()
        {
            var acc = new Account
            {
                UserId = UserId,
                Username = Username,
                DisplayName = DisplayName,
                Group = string.IsNullOrWhiteSpace(Group) ? "Default" : Group,
                BrowserTrackerId = BrowserTrackerId,
                Fields = Fields ?? new(),
                LastUse = LastUse,
                Alias = Alias,
                Description = Description,
                ProxyUrl = ProxyUrl ?? "",
                FFlags = FFlags ?? "",
                AutoRejoin = AutoRejoin,
                IsFavorite = IsFavorite,
                Color = Color ?? "",
                AddedUtc = AddedUtc,
                CookieUpdatedUtc = CookieUpdatedUtc,
                LastValidatedUtc = LastValidatedUtc,
                CookieRejectedUtc = CookieRejectedUtc,
            };

            if (Crypto.TryUnprotectString(Cookie, out var cookie)) acc.Cookie = cookie;
            else acc.UnreadableCookie = Cookie;

            if (Crypto.TryUnprotectString(TotpSecret, out var totp)) acc.TotpSecret = totp;
            else acc.UnreadableTotp = TotpSecret;

            return acc;
        }
    }

    public void SetMasterPassword(string? password)
    {
        MasterPassword = string.IsNullOrEmpty(password) ? null : password;
        Save();
    }

    /// <summary>Constant-time check used by the lock screen.</summary>
    public bool VerifyMasterPassword(string? candidate)
        => MasterPassword is { Length: > 0 } && Crypto.SecretEquals(candidate, MasterPassword);

    // ---------------------------------------------------------------
    //  Add / import
    // ---------------------------------------------------------------
    public record AddResult(bool Success, string Message, Account? Account);

    public async Task<AddResult> AddByCookieAsync(string rawCookie)
    {
        string cookie = ExtractCookie(rawCookie);
        if (string.IsNullOrWhiteSpace(cookie))
            return new(false, L.T("Add.Cookie.NotACookie"), null);

        var (identity, rejected) = await RobloxApi.GetAuthenticatedUserDetailedAsync(cookie);
        if (identity == null)
            return new(false, rejected ? L.T("Add.Cookie.Rejected") : L.T("Add.Cookie.Unreachable"), null);

        var existing = Accounts.FirstOrDefault(a => a.UserId == identity.Id);
        if (existing != null)
        {
            existing.ReplaceCookie(cookie);
            existing.UnreadableCookie = null;
            existing.MarkValidated(true);
            if (string.IsNullOrEmpty(existing.Username)) existing.Username = identity.Name;
            Save();
            AuditLogService.Log(AuditLogService.Category.Cookie, $"Cookie refreshed for {identity.Name} (userId {identity.Id})");
            return new(true, L.T("Add.Cookie.Refreshed", identity.Name), existing);
        }

        var now = DateTime.UtcNow;
        var acc = new Account
        {
            Cookie = cookie,
            UserId = identity.Id,
            Username = identity.Name,
            DisplayName = identity.DisplayName,
            LastUse = DateTime.Now,
            AddedUtc = now,
            CookieUpdatedUtc = now,
            LastValidatedUtc = now,
        };
        Accounts.Add(acc);
        Save();
        AuditLogService.Log(AuditLogService.Category.Account, $"Account added: {identity.Name} (userId {identity.Id})");
        return new(true, L.T("Add.Cookie.Added", identity.Name), acc);
    }

    /// <summary>Bulk import: pull every cookie out of arbitrary pasted text and add each.</summary>
    public async Task<(int added, int failed)> ImportManyAsync(string text, Action<string>? progress = null)
    {
        var cookies = ExtractCookies(text);
        int added = 0, failed = 0;
        foreach (var c in cookies)
        {
            progress?.Invoke(L.T("Import.Progress", added + failed + 1, cookies.Count));
            var r = await AddByCookieAsync(c);
            if (r.Success) added++; else failed++;
        }
        return (added, failed);
    }

    public void Remove(Account account)
    {
        Accounts.Remove(account);
        Save();
        AuditLogService.Log(AuditLogService.Category.Account, $"Account removed: {account.Username} (userId {account.UserId})");
    }

    public IEnumerable<string> Groups =>
        Accounts.Select(a => a.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Moves every account of one group into another (rename or merge). Returns how many moved.</summary>
    public int RenameGroup(string from, string to)
    {
        to = string.IsNullOrWhiteSpace(to) ? "Default" : to.Trim();
        int n = 0;
        foreach (var a in Accounts.Where(a => string.Equals(a.Group, from, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            a.Group = to;
            n++;
        }
        if (n > 0) Save();
        return n;
    }

    /// <summary>Proxy configured for the account that owns this cookie, if any (per-account proxy routing).</summary>
    public string? ProxyForCookie(string cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        try
        {
            foreach (var a in Accounts.ToArray())
                if (a.Cookie == cookie)
                    return string.IsNullOrWhiteSpace(a.ProxyUrl) ? null : a.ProxyUrl;
        }
        catch { /* collection changed mid-read on the UI thread — fall back to the global proxy */ }
        return null;
    }

    // ---------------------------------------------------------------
    //  Live refresh
    // ---------------------------------------------------------------
    public async Task RefreshLiveDataAsync(IEnumerable<Account>? subset = null)
    {
        var settings = SettingsService.Current;
        var accounts = (subset ?? Accounts).ToList();
        if (accounts.Count == 0) return;

        if (settings.ShowThumbnails)
        {
            var shots = await RobloxApi.GetHeadshotsAsync(accounts.Select(a => a.UserId));
            foreach (var a in accounts)
                if (shots.TryGetValue(a.UserId, out var url)) a.ThumbnailUrl = url;
        }

        if (settings.ShowPresence)
            await ApplyPresenceAsync(accounts);

        // Robux, RAP and Premium are per-account calls. A few in parallel keeps a large list quick
        // without tripping Roblox's rate limit; accounts whose cookie was rejected are skipped —
        // those calls can only fail.
        var live = accounts.Where(a => a.IsValid && !string.IsNullOrEmpty(a.Cookie)).ToList();
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(live.Select(async a =>
        {
            await gate.WaitAsync();
            try
            {
                if (settings.ShowRobux)
                {
                    long rbx = await RobloxApi.GetRobuxAsync(a.Cookie);
                    if (rbx >= 0) a.Robux = rbx;
                }
                if (settings.TrackEconomy)
                {
                    var (rap, _) = await RobloxApi.GetCollectiblesRapAsync(a.Cookie, a.UserId);
                    if (rap >= 0) a.Rap = rap;
                    a.IsPremium = await RobloxApi.GetPremiumAsync(a.Cookie, a.UserId);
                }
            }
            catch (Exception ex) { DiagnosticsService.Warn("store", $"Live data refresh failed for {a.Username}", ex); }
            finally { gate.Release(); }
        }));
    }

    /// <summary>Presence-only refresh for the live timer: one authenticated batch call per 50 accounts.</summary>
    public Task RefreshPresenceOnlyAsync() => ApplyPresenceAsync(Accounts.ToList());

    private static async Task ApplyPresenceAsync(List<Account> accounts)
    {
        if (accounts.Count == 0) return;
        var authCookie = accounts.FirstOrDefault(a => a.IsValid && !string.IsNullOrEmpty(a.Cookie))?.Cookie;
        if (authCookie == null) return;

        var pres = await RobloxApi.GetPresenceDetailsAsync(authCookie, accounts.Select(a => a.UserId));
        if (pres.Count == 0) return;   // request failed — keep the last known state instead of flashing "offline"

        foreach (var a in accounts)
        {
            if (pres.TryGetValue(a.UserId, out var p))
            {
                a.Presence = p.Status;
                a.PlaceId = p.PlaceId;
                a.RootPlaceId = p.RootPlaceId;
                a.GameId = p.JobId;
                a.LastLocation = p.Status == PresenceStatus.InGame ? p.LastLocation : "";
            }
            else
            {
                a.Presence = PresenceStatus.Offline;
                a.PlaceId = 0;
                a.RootPlaceId = 0;
                a.GameId = null;
                a.LastLocation = "";
            }
        }
    }

    public async Task RefreshIdentityAsync(Account acc)
    {
        var (id, rejected) = await RobloxApi.GetAuthenticatedUserDetailedAsync(acc.Cookie);
        if (id != null)
        {
            acc.UserId = id.Id;
            acc.Username = id.Name;
            acc.DisplayName = id.DisplayName;
            acc.MarkValidated(true);
            Save();
        }
        else if (rejected)
        {
            acc.MarkValidated(false);
            Save();
        }
    }

    // ---------------------------------------------------------------
    //  Cookie extraction
    // ---------------------------------------------------------------
    private static readonly Regex CookieRegex =
        new(@"_\|WARNING:-DO-NOT-SHARE-THIS\.[^""'\s;,]+", RegexOptions.Compiled);

    internal static string ExtractCookie(string raw)
    {
        raw = (raw ?? "").Trim();
        var m = CookieRegex.Match(raw);
        if (m.Success) return m.Value;
        // Allow a bare token (no warning prefix) as long as it looks like one.
        if (raw.Length > 200 && !raw.Any(char.IsWhiteSpace)) return raw;
        return "";
    }

    private static List<string> ExtractCookies(string text)
    {
        var list = CookieRegex.Matches(text).Select(m => m.Value).ToList();
        if (list.Count == 0)
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var c = ExtractCookie(line);
                if (!string.IsNullOrEmpty(c)) list.Add(c);
            }
        }
        return list.Distinct().ToList();
    }
}
