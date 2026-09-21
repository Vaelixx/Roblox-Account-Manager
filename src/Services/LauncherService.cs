using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Web;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public static class LauncherService
{
    // Last client spawned per account, so a relaunch can close it. Concurrent: written from delayed
    // attribution tasks, read from the UI thread and the "close all" hotkey.
    private static readonly ConcurrentDictionary<long, int> _lastProcess = new();

    private static AccountStore? _store;
    public static void Init(AccountStore store) => _store = store;

    /// <summary>
    /// Persists a cookie Roblox rotated during the auth-ticket call. Without this an account whose
    /// cookie was rotated eventually stops launching. Never logs the value.
    /// </summary>
    private static void PersistRotatedCookie(Account acc, string? rotated)
    {
        if (string.IsNullOrEmpty(rotated) || rotated == acc.Cookie) return;
        if (!SettingsService.Current.RotationDetectionEnabled) return;
        acc.ReplaceCookie(rotated);
        try { _store?.Save(); } catch { }
        AuditLogService.Log(AuditLogService.Category.Rotation, $"Cookie rotated for {acc.DisplayNameOrUser} (userId {acc.UserId})");
    }

    public static void EnsureMultiInstance(bool enabled)
    {
        if (enabled) RobloxSingletonService.Apply();
        else RobloxSingletonService.Stop();
    }

    public static void ReleaseMultiInstance() => RobloxSingletonService.Stop();

    public static string EnsureTrackerId(Account acc)
    {
        if (string.IsNullOrEmpty(acc.BrowserTrackerId))
            acc.BrowserTrackerId = Random.Shared.Next(100000, 175000).ToString()
                                 + Random.Shared.Next(100000, 900000).ToString();
        return acc.BrowserTrackerId;
    }

    public class LaunchResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public Task<int?>? AttributionTask { get; init; }

        public static LaunchResult Ok(Task<int?>? attributionTask = null) => new()
        {
            Success = true,
            Message = L.T("Launch.Launched"),
            AttributionTask = attributionTask,
        };
        public static LaunchResult Fail(string m) => new() { Success = false, Message = m };
    }

    /// <summary>Everything that identifies where a launch should land.</summary>
    public sealed record JoinTarget(long PlaceId, string? JobId = null, long FollowUserId = 0, string? LinkCode = null, string? AccessCode = null);

    /// <summary>
    /// Builds the placelauncherurl the client parses. The client reads the query parameters itself
    /// (it does not fetch this URL), which is why the long-standing assetgame form keeps working.
    /// </summary>
    public static string PlaceLauncherUrl(JoinTarget t, string tracker)
    {
        const string Base = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

        if (t.FollowUserId > 0)
            return $"{Base}?request=RequestFollowUser&userId={t.FollowUserId}";

        if (!string.IsNullOrEmpty(t.LinkCode) || !string.IsNullOrEmpty(t.AccessCode))
            return $"{Base}?request=RequestPrivateGame&browserTrackerId={tracker}&placeId={t.PlaceId}"
                 + $"&accessCode={Uri.EscapeDataString(t.AccessCode ?? "")}"
                 + $"&linkCode={Uri.EscapeDataString(t.LinkCode ?? "")}"
                 + "&isPlayTogetherGame=false";

        if (!string.IsNullOrEmpty(t.JobId))
            return $"{Base}?request=RequestGameJob&browserTrackerId={tracker}&placeId={t.PlaceId}"
                 + $"&gameId={Uri.EscapeDataString(t.JobId)}&isPlayTogetherGame=false";

        return $"{Base}?request=RequestGame&browserTrackerId={tracker}&placeId={t.PlaceId}&isPlayTogetherGame=false";
    }

    /// <param name="jobId">optional specific server</param>
    /// <param name="followUserId">optional user to follow into their game</param>
    public static Task<LaunchResult> LaunchAsync(Account acc, long placeId, string? jobId = null, long followUserId = 0,
        string? privateLinkCode = null, string? accessCode = null)
        => LaunchAsync(acc, new JoinTarget(placeId, jobId, followUserId, privateLinkCode, accessCode));

    public static async Task<LaunchResult> LaunchAsync(Account acc, JoinTarget target)
    {
        if (LockService.IsLocked) return LaunchResult.Fail(L.T("Lock.Blocked"));
        if (string.IsNullOrEmpty(acc.Cookie)) return LaunchResult.Fail(L.T("Launch.NoCookie"));

        var settings = SettingsService.Current;
        EnsureMultiInstance(settings.EnableMultiInstance);

        // Frame-rate cap, FastFlags and this account's own overrides, before the process starts.
        try { FFlagsService.ApplyForLaunch(settings, acc); } catch { }

        string tracker = EnsureTrackerId(acc);

        var (ticket, rotated, error) = await RobloxApi.GetAuthTicketDetailedAsync(acc.Cookie);
        if (string.IsNullOrEmpty(ticket))
        {
            // Only an outright rejection (401/403) may mark the account invalid — a 429 from
            // launching several accounts at once must not condemn them all.
            var (identity, rejected) = await RobloxApi.GetAuthenticatedUserDetailedAsync(acc.Cookie);
            if (identity == null && rejected)
            {
                acc.MarkValidated(false);
                try { _store?.Save(); } catch { }
                return LaunchResult.Fail(L.T("Launch.CookieExpired"));
            }
            if (identity == null)
                return LaunchResult.Fail(L.T("Launch.Unreachable", error));

            acc.MarkValidated(true);
            return LaunchResult.Fail(L.T("Launch.NoTicket", error));
        }
        acc.MarkValidated(true);
        PersistRotatedCookie(acc, rotated);

        if (settings.AutoCloseLastProcess) CloseLast(acc);

        long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Sanitise a pasted Job ID: stray whitespace, quotes and commas from copy-paste.
        string? jobId = target.JobId?.Trim().Trim('"', '\'', ',', ' ');
        if (string.IsNullOrWhiteSpace(jobId)) jobId = null;
        target = target with { JobId = jobId };

        string uri = "roblox-player:1"
            + "+launchmode:play"
            + $"+gameinfo:{ticket}"
            + $"+launchtime:{launchTime}"
            + $"+placelauncherurl:{HttpUtility.UrlEncode(PlaceLauncherUrl(target, tracker))}"
            + $"+browsertrackerid:{tracker}"
            + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

        try
        {
            // Only clients that appear after this instant can belong to this launch. Backdated a
            // little: the protocol handler may have spawned the client before Process.Start returns.
            DateTime launchedAt = DateTime.Now.AddSeconds(-2);

            using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { }
            acc.LastUse = DateTime.Now;

            _ = Task.Run(async () =>
            {
                // Presence flips to "In Game" a few seconds after the join; poll twice so the
                // dashboard catches it quickly.
                await Task.Delay(4000);
                try { await PresenceService.PollNowAsync(); } catch { }
                await Task.Delay(6000);
                try { await PresenceService.PollNowAsync(); } catch { }
            });

            Task<int?> attributionTask = Task.Run(() => AttributeClientAsync(acc, target.PlaceId, jobId, launchedAt));

            try { PluginService.RaiseLaunched(acc, target.PlaceId, jobId); } catch { }
            if (settings.ToastOnLaunch)
                ToastService.Success(L.T("Toast.Launched.Title"), L.T("Toast.Launched.Body", acc.DisplayNameOrUser));
            if (settings.NotifyOnConnect && WebhookService.Configured)
                WebhookService.Connected(acc, target.PlaceId, jobId);
            AuditLogService.Log(AuditLogService.Category.Launch, $"Launched {acc.DisplayNameOrUser} into place {target.PlaceId}");
            return LaunchResult.Ok(attributionTask);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(ExplainLaunchFailure(ex));
        }
    }

    /// <summary>
    /// Binds the client this launch produced to its account. Retried: on a cold start (shader cache,
    /// a pending Roblox update, a slow disk) the client can take far longer than a few seconds to
    /// exist, and a single miss meant no Anti-AFK, crash watchdog or RAM cap for it.
    /// </summary>
    private static async Task<int?> AttributeClientAsync(Account acc, long placeId, string? jobId, DateTime launchedAt)
    {
        await Task.Delay(4000);

        int pid = 0;
        for (int attempt = 0; attempt < 15 && pid == 0; attempt++)
        {
            try { pid = ProcessRegistry.RegisterNewest(acc, placeId, jobId, launchedAt); } catch { }
            if (pid == 0) await Task.Delay(2000);
        }

        if (pid != 0) _lastProcess[acc.UserId] = pid;
        else DiagnosticsService.Warn("launcher", $"No client could be attributed to {acc.DisplayNameOrUser} within 34s of launch");
        return pid == 0 ? null : pid;
    }

    /// <summary>
    /// Turns a Process.Start failure on the roblox-player: URI into something actionable. The raw
    /// message ("The system cannot find the file specified") points at nothing; the cause is almost
    /// always a missing or hijacked protocol handler.
    /// </summary>
    private static string ExplainLaunchFailure(Exception ex)
        => ex is System.ComponentModel.Win32Exception or FileNotFoundException
            ? L.T("Launch.NoHandler")
            : L.T("Launch.Failed", ex.Message);

    /// <summary>Opens the Roblox app itself (home screen), signed in as this account — no game.</summary>
    public static async Task<LaunchResult> OpenRobloxAppAsync(Account acc)
    {
        if (LockService.IsLocked) return LaunchResult.Fail(L.T("Lock.Blocked"));
        EnsureMultiInstance(SettingsService.Current.EnableMultiInstance);
        string tracker = EnsureTrackerId(acc);

        var (ticket, rotated, error) = await RobloxApi.GetAuthTicketDetailedAsync(acc.Cookie);
        if (string.IsNullOrEmpty(ticket))
        {
            var (identity, rejected) = await RobloxApi.GetAuthenticatedUserDetailedAsync(acc.Cookie);
            if (identity == null && rejected)
            {
                acc.MarkValidated(false);
                return LaunchResult.Fail(L.T("Launch.CookieExpired"));
            }
            return LaunchResult.Fail(L.T("Launch.NoTicket", error));
        }
        acc.MarkValidated(true);
        PersistRotatedCookie(acc, rotated);

        long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string uri = "roblox-player:1+launchmode:app"
            + $"+gameinfo:{ticket}+launchtime:{launchTime}+browsertrackerid:{tracker}"
            + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
        try
        {
            DateTime launchedAt = DateTime.Now.AddSeconds(-2);
            using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { }
            acc.LastUse = DateTime.Now;
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                try { await PresenceService.PollNowAsync(); } catch { }
            });
            Task<int?> attributionTask = Task.Run(() => AttributeClientAsync(acc, 0, null, launchedAt));
            if (SettingsService.Current.ToastOnLaunch)
                ToastService.Success(L.T("Toast.Launched.Title"), L.T("Toast.Launched.Body", acc.DisplayNameOrUser));
            return LaunchResult.Ok(attributionTask);
        }
        catch (Exception ex) { return LaunchResult.Fail(ExplainLaunchFailure(ex)); }
    }

    private static void CloseLast(Account acc)
    {
        if (!_lastProcess.TryGetValue(acc.UserId, out int pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited && p.ProcessName.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase))
            {
                // A deliberate close, not a crash: flag it so the watchdog does not answer it with an
                // auto-rejoin (the session is still booked as playtime).
                ProcessRegistry.MarkClosing(pid);
                p.CloseMainWindow();
                if (!p.WaitForExit(1500)) p.Kill();
            }
        }
        catch { }
        _lastProcess.TryRemove(acc.UserId, out _);
    }

    /// <summary>Closes every running Roblox client and returns how many were closed.</summary>
    public static int CloseAllClients()
    {
        int closed = 0;
        foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try
            {
                if (!p.HasExited)
                {
                    // Deliberate close: flag it so the watchdog does not auto-rejoin it.
                    ProcessRegistry.MarkClosing(p.Id);
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1500)) p.Kill();
                    closed++;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        _lastProcess.Clear();
        try { ProcessRegistry.Prune(); } catch { }
        return closed;
    }
}
