using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public class AccountStore
{
    private static readonly string StorePath = Paths.InData("accounts.dat");
    private static readonly string BackupPath = Paths.InData("accounts.bak");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // Serialises disk writes: Save() can be triggered from the UI thread and from
    // WebApiService's background HttpListener callbacks (rotated-cookie persistence) at
    // the same time; without this two interleaved saves can corrupt the store or backup.
    private static readonly object _saveLock = new();

    public ObservableCollection<Account> Accounts { get; } = new();

    /// <summary>null = no master password (DPAPI mode). Non-null = current master password.</summary>
    public string? MasterPassword { get; private set; }

    public bool IsPasswordProtected
    {
        get
        {
            if (!File.Exists(StorePath)) return false;
            try { return Crypto.IsPasswordProtected(File.ReadAllBytes(StorePath)); }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------
    //  Persistence
    // ---------------------------------------------------------------
    public bool StoreExists => File.Exists(StorePath);

    /// <summary>Loads accounts. Returns false if a (wrong/missing) password blocked decryption.</summary>
    public bool Load(string? password)
    {
        Accounts.Clear();
        if (!File.Exists(StorePath)) { MasterPassword = password; return true; }

        try
        {
            byte[] data = File.ReadAllBytes(StorePath);
            byte[] plain = Crypto.Decrypt(data, password);
            MasterPassword = Crypto.IsPasswordProtected(data) ? password : null;

            string json = Encoding.UTF8.GetString(plain);
            var list = JsonSerializer.Deserialize<List<Persisted>>(json) ?? new();
            foreach (var p in list) Accounts.Add(p.ToAccount());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save()
    {
        try
        {
            // The cookie is DPAPI-encrypted per account, so it is NEVER written as plain text —
            // not even inside the (already encrypted) store JSON. Snapshot + serialise up front
            // so the (potentially expensive) crypto happens before we hold the write lock.
            var dtos = Accounts.Select(Persisted.FromAccount).ToList();
            string json = JsonSerializer.Serialize(dtos, JsonOpts);
            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = MasterPassword is { Length: > 0 }
                ? Crypto.EncryptPassword(plain, MasterPassword)
                : Crypto.EncryptDpapi(plain);

            lock (_saveLock)
            {
                Directory.CreateDirectory(Paths.DataDir);
                if (File.Exists(StorePath))
                    File.Copy(StorePath, BackupPath, overwrite: true);

                // Atomic write: stage to a temp file, then swap it into place so a crash or
                // power-loss mid-write can never leave a truncated/corrupt accounts.dat.
                string tmp = StorePath + ".tmp";
                File.WriteAllBytes(tmp, encrypted);
                if (File.Exists(StorePath))
                    File.Replace(tmp, StorePath, null);
                else
                    File.Move(tmp, StorePath);
            }
        }
        catch { /* best-effort; backup remains */ }
    }

    /// <summary>On-disk shape. Cookie is stored DPAPI-protected (enc1:…), never raw.</summary>
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
        public string TotpSecret { get; set; } = "";       // DPAPI-protected, like Cookie
        public string ProxyUrl { get; set; } = "";
        public string FFlags { get; set; } = "";
        public bool AutoRejoin { get; set; }
        public bool IsFavorite { get; set; }

        public static Persisted FromAccount(Account a) => new()
        {
            Cookie = Crypto.ProtectString(a.Cookie),
            UserId = a.UserId,
            Username = a.Username,
            DisplayName = a.DisplayName,
            Group = a.Group,
            BrowserTrackerId = a.BrowserTrackerId,
            Fields = a.Fields,
            LastUse = a.LastUse,
            Alias = a.Alias,
            Description = a.Description,
            TotpSecret = Crypto.ProtectString(a.TotpSecret),
            ProxyUrl = a.ProxyUrl,
            FFlags = a.FFlags,
            AutoRejoin = a.AutoRejoin,
            IsFavorite = a.IsFavorite
        };

        public Account ToAccount() => new()
        {
            Cookie = Crypto.UnprotectString(Cookie),
            UserId = UserId,
            Username = Username,
            DisplayName = DisplayName,
            Group = string.IsNullOrWhiteSpace(Group) ? "Default" : Group,
            BrowserTrackerId = BrowserTrackerId,
            Fields = Fields ?? new(),
            LastUse = LastUse,
            Alias = Alias,
            Description = Description,
            TotpSecret = Crypto.UnprotectString(TotpSecret),
            ProxyUrl = ProxyUrl ?? "",
            FFlags = FFlags ?? "",
            AutoRejoin = AutoRejoin,
            IsFavorite = IsFavorite
        };
    }

    public void SetMasterPassword(string? password)
    {
        MasterPassword = string.IsNullOrEmpty(password) ? null : password;
        Save();
    }

    // ---------------------------------------------------------------
    //  Add / import
    // ---------------------------------------------------------------
    public record AddResult(bool Success, string Message, Account? Account);

    public async Task<AddResult> AddByCookieAsync(string rawCookie)
    {
        string cookie = ExtractCookie(rawCookie);
        if (string.IsNullOrWhiteSpace(cookie))
            return new(false, "That doesn't look like a valid .ROBLOSECURITY cookie.", null);

        var identity = await RobloxApi.GetAuthenticatedUserAsync(cookie);
        if (identity == null)
            return new(false, "Cookie rejected by Roblox — it may be expired or invalid.", null);

        var existing = Accounts.FirstOrDefault(a => a.UserId == identity.Id);
        if (existing != null)
        {
            existing.Cookie = cookie;      // refresh the token on a known account
            existing.IsValid = true;
            Save();
            return new(false, $"{identity.Name} is already in your list — its cookie was refreshed.", existing);
        }

        var acc = new Account
        {
            Cookie = cookie,
            UserId = identity.Id,
            Username = identity.Name,
            DisplayName = identity.DisplayName,
            IsValid = true,
            LastUse = DateTime.Now
        };
        Accounts.Add(acc);
        Save();
        return new(true, $"Added {identity.Name}.", acc);
    }

    /// <summary>Bulk import: pull every cookie out of arbitrary pasted text and add each.</summary>
    public async Task<(int added, int failed)> ImportManyAsync(string text, Action<string>? progress = null)
    {
        var cookies = ExtractCookies(text);
        int added = 0, failed = 0;
        foreach (var c in cookies)
        {
            progress?.Invoke($"Validating… ({added + failed + 1}/{cookies.Count})");
            var r = await AddByCookieAsync(c);
            if (r.Success) added++; else failed++;
        }
        return (added, failed);
    }

    public void Remove(Account account)
    {
        Accounts.Remove(account);
        Save();
    }

    public IEnumerable<string> Groups =>
        Accounts.Select(a => a.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().OrderBy(g => g);

    // ---------------------------------------------------------------
    //  Live refresh
    // ---------------------------------------------------------------
    public async Task RefreshLiveDataAsync(IEnumerable<Account>? subset = null)
    {
        var settings = SettingsService.Current;
        var accounts = (subset ?? Accounts).ToList();
        if (accounts.Count == 0) return;

        // Thumbnails (single batched call, no auth needed)
        if (settings.ShowThumbnails)
        {
            var shots = await RobloxApi.GetHeadshotsAsync(accounts.Select(a => a.UserId));
            foreach (var a in accounts)
                if (shots.TryGetValue(a.UserId, out var url)) a.ThumbnailUrl = url;
        }

        // Presence (one authed call using any valid account's cookie)
        if (settings.ShowPresence)
        {
            var authCookie = accounts.FirstOrDefault(a => a.IsValid)?.Cookie;
            if (authCookie != null)
            {
                var pres = await RobloxApi.GetPresencesAsync(authCookie, accounts.Select(a => a.UserId));
                foreach (var a in accounts)
                    a.Presence = pres.TryGetValue(a.UserId, out var p) ? p : "Offline";
            }
        }

        // Robux (per account)
        if (settings.ShowRobux)
        {
            foreach (var a in accounts)
            {
                long rbx = await RobloxApi.GetRobuxAsync(a.Cookie);
                if (rbx >= 0) a.Robux = rbx;
            }
        }

        // Economy: collectible RAP + premium membership (heavier, one pass per account).
        if (settings.TrackEconomy)
        {
            foreach (var a in accounts)
            {
                if (!a.IsValid) continue;
                var (rap, _) = await RobloxApi.GetCollectiblesRapAsync(a.Cookie, a.UserId);
                if (rap >= 0) a.Rap = rap;
                a.IsPremium = await RobloxApi.GetPremiumAsync(a.Cookie, a.UserId);
            }
        }
    }

    /// <summary>
    /// Lightweight presence-only refresh for the live dashboard timer. Skips the
    /// thumbnail and robux calls so it can run on a short cadence without hammering
    /// the economy endpoint; a single authed presence call covers every account.
    /// </summary>
    public async Task RefreshPresenceOnlyAsync()
    {
        var accounts = Accounts.ToList();
        if (accounts.Count == 0) return;
        var authCookie = accounts.FirstOrDefault(a => a.IsValid)?.Cookie;
        if (authCookie == null) return;
        var pres = await RobloxApi.GetPresenceDetailsAsync(authCookie, accounts.Select(a => a.UserId));
        foreach (var a in accounts)
        {
            if (pres.TryGetValue(a.UserId, out var p))
            {
                a.Presence = p.Status;
                a.PlaceId = p.PlaceId;
                a.RootPlaceId = p.RootPlaceId;
            }
            else
            {
                a.Presence = "Offline";
                a.PlaceId = 0;
                a.RootPlaceId = 0;
            }
        }
    }

    public async Task RefreshIdentityAsync(Account acc)
    {
        var id = await RobloxApi.GetAuthenticatedUserAsync(acc.Cookie);
        if (id != null)
        {
            acc.UserId = id.Id;
            acc.Username = id.Name;
            acc.DisplayName = id.DisplayName;
            acc.IsValid = true;
            acc.RaiseIdentityChanged();
            Save();
        }
        else acc.IsValid = false;
    }

    // ---------------------------------------------------------------
    //  Cookie extraction
    // ---------------------------------------------------------------
    private static readonly Regex CookieRegex =
        new(@"_\|WARNING:-DO-NOT-SHARE-THIS\.[^""'\s]+", RegexOptions.Compiled);

    private static string ExtractCookie(string raw)
    {
        raw = raw.Trim();
        var m = CookieRegex.Match(raw);
        if (m.Success) return m.Value;
        // Allow pasting a bare token (no warning prefix).
        if (raw.Length > 200 && !raw.Contains(' ') && !raw.Contains('\n')) return raw;
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
