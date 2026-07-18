using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public static class LauncherService
{
    // Held open so Roblox's own single-instance check passes for every extra client.
    private static Mutex? _multiMutex;

    // Remembers the last client we spawned per account so we can close it on relaunch.
    // Concurrent because it's written from delayed launch tasks and read/cleared from the
    // UI thread (relaunch) and the global "close all" hotkey — potentially at the same time.
    private static readonly ConcurrentDictionary<long, int> _lastProcess = new();

    // Shared store so a rotated .ROBLOSECURITY captured during launch can be persisted.
    private static AccountStore? _store;
    public static void Init(AccountStore store) => _store = store;

    /// <summary>
    /// Persist a rotated cookie surfaced by the auth-ticket call. No-op unless rotation
    /// detection is enabled and the value actually changed. Updates the live account, saves
    /// the store, and records the event in the audit log — the cookie value is never logged.
    /// </summary>
    private static void PersistRotatedCookie(Account acc, string? rotated)
    {
        if (string.IsNullOrEmpty(rotated) || rotated == acc.Cookie) return;
        if (!SettingsService.Current.RotationDetectionEnabled) return;
        acc.Cookie = rotated;
        try { _store?.Save(); } catch { }
        AuditLogService.Log(AuditLogService.Category.Rotation,
            $"Cookie rotated for {acc.DisplayNameOrUser} (userId {acc.UserId})");
    }

    public static void EnsureMultiInstance(bool enabled)
    {
        if (enabled && _multiMutex == null)
        {
            try
            {
                _multiMutex = new Mutex(true, "ROBLOX_singletonMutex", out bool created);
                if (!created) _multiMutex.WaitOne(TimeSpan.Zero);
            }
            catch { _multiMutex = null; }
        }
        else if (!enabled && _multiMutex != null)
        {
            ReleaseMultiInstance();
        }
    }

    public static void ReleaseMultiInstance()
    {
        try { _multiMutex?.ReleaseMutex(); } catch { }
        try { _multiMutex?.Dispose(); } catch { }
        _multiMutex = null;
    }

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
        public static LaunchResult Ok() => new() { Success = true, Message = "Launched" };
        public static LaunchResult Fail(string m) => new() { Success = false, Message = m };
    }

    /// <param name="jobId">optional specific server</param>
    /// <param name="followUserId">optional user to follow into their game</param>
    public static async Task<LaunchResult> LaunchAsync(Account acc, long placeId, string? jobId = null, long followUserId = 0, string? privateLinkCode = null, string? accessCode = null)
    {
        var settings = SettingsService.Current;
        EnsureMultiInstance(settings.EnableMultiInstance);

        if (settings.UnlockFps)
            TryPatchFps(settings.MaxFps);

        string tracker = EnsureTrackerId(acc);

        var (ticket, rotated, error) = await RobloxApi.GetAuthTicketDetailedAsync(acc.Cookie);
        if (string.IsNullOrEmpty(ticket))
        {
            // Distinguish a genuinely dead cookie from a transient ticket failure.
            var identity = await RobloxApi.GetAuthenticatedUserAsync(acc.Cookie);
            if (identity == null)
            {
                acc.IsValid = false;
                return LaunchResult.Fail("This cookie is no longer valid — re-add the account.");
            }

            acc.IsValid = true; // cookie is fine; the ticket call hiccuped
            return LaunchResult.Fail($"Couldn't get a launch ticket ({error}). Cookie is still valid — try again in a moment.");
        }
        acc.IsValid = true;
        PersistRotatedCookie(acc, rotated);

        if (settings.AutoCloseLastProcess) CloseLast(acc);

        long launchTime = (long)Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds);

        // Sanitise a pasted Job ID: trim stray whitespace/quotes/commas from copy-paste. Blank -> normal join.
        jobId = jobId?.Trim().Trim('"', '\'', ',', ' ');
        if (string.IsNullOrWhiteSpace(jobId)) jobId = null;

        string placeLauncherUrl;
        if (followUserId > 0)
        {
            placeLauncherUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={followUserId}";
        }
        else if (!string.IsNullOrEmpty(privateLinkCode) || !string.IsNullOrEmpty(accessCode))
        {
            // Private server: joined via its shared link code and/or access code (owned VIP server).
            placeLauncherUrl = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame"
                             + $"&browserTrackerId={tracker}&placeId={placeId}"
                             + $"&accessCode={Uri.EscapeDataString(accessCode ?? "")}"
                             + $"&linkCode={Uri.EscapeDataString(privateLinkCode ?? "")}"
                             + "&isPlayTogetherGame=false";
        }
        else if (!string.IsNullOrEmpty(jobId))
        {
            placeLauncherUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGameJob&browserTrackerId={tracker}&placeId={placeId}&gameId={Uri.EscapeDataString(jobId)}&isPlayTogetherGame=false";
        }
        else
        {
            placeLauncherUrl = $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId={tracker}&placeId={placeId}&isPlayTogetherGame=false";
        }

        string uri = "roblox-player:1"
            + $"+launchmode:play"
            + $"+gameinfo:{ticket}"
            + $"+launchtime:{launchTime}"
            + $"+placelauncherurl:{HttpUtility.UrlEncode(placeLauncherUrl)}"
            + $"+browsertrackerid:{tracker}"
            + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

        try
        {
            var psi = new ProcessStartInfo(uri) { UseShellExecute = true };
            Process.Start(psi);
            acc.LastUse = DateTime.Now;

            // Give the protocol handler a moment, then remember the freshest client for this
            // account — both for our own CloseLast bookkeeping and for the process registry that
            // feeds Anti-AFK, the crash watchdog (needs place/job to re-join) and the RAM monitor.
            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                RememberNewest(acc);
                try { ProcessRegistry.RegisterNewest(acc, placeId, jobId); } catch { }
            });

            try { PluginService.RaiseLaunched(acc, placeId, jobId); } catch { }
            if (SettingsService.Current.ToastOnLaunch)
                ToastService.Success("Launched", $"{acc.DisplayNameOrUser} is starting up.");
            if (SettingsService.Current.NotifyOnConnect && WebhookService.Configured)
                WebhookService.Connected(acc, placeId, jobId);
            return LaunchResult.Ok();
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail($"Failed to launch Roblox: {ex.Message}");
        }
    }

    /// <summary>Opens the Roblox app itself (home screen), signed in as this account — no game.</summary>
    public static async Task<LaunchResult> OpenRobloxAppAsync(Account acc)
    {
        EnsureMultiInstance(SettingsService.Current.EnableMultiInstance);
        string tracker = EnsureTrackerId(acc);

        var (ticket, rotated, error) = await RobloxApi.GetAuthTicketDetailedAsync(acc.Cookie);
        if (string.IsNullOrEmpty(ticket))
        {
            var identity = await RobloxApi.GetAuthenticatedUserAsync(acc.Cookie);
            if (identity == null) { acc.IsValid = false; return LaunchResult.Fail("This cookie is no longer valid — re-add the account."); }
            return LaunchResult.Fail($"Couldn't get a launch ticket ({error}). Try again in a moment.");
        }
        PersistRotatedCookie(acc, rotated);

        long launchTime = (long)Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds);
        string uri = "roblox-player:1+launchmode:app"
            + $"+gameinfo:{ticket}+launchtime:{launchTime}+browsertrackerid:{tracker}"
            + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            acc.LastUse = DateTime.Now;
            if (SettingsService.Current.ToastOnLaunch)
                ToastService.Success("Launched", $"{acc.DisplayNameOrUser} is starting up.");
            return LaunchResult.Ok();
        }
        catch (Exception ex) { return LaunchResult.Fail($"Failed to open Roblox: {ex.Message}"); }
    }

    private static void CloseLast(Account acc)
    {
        if (_lastProcess.TryGetValue(acc.UserId, out int pid))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited && p.ProcessName.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    // Give the graceful close a brief chance to take effect before forcing it.
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1500)) p.Kill();
                }
            }
            catch { }
            _lastProcess.TryRemove(acc.UserId, out _);
        }
    }

    /// <summary>
    /// Kills every running Roblox client and returns how many were closed. Used by the
    /// global "Close all Roblox" hotkey; also clears the per-account "last process" map
    /// so a later relaunch doesn't try to close an already-dead pid.
    /// </summary>
    public static int CloseAllClients()
    {
        int closed = 0;
        var procs = Process.GetProcessesByName("RobloxPlayerBeta");
        foreach (var p in procs)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1500)) p.Kill();
                    closed++;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        _lastProcess.Clear();
        return closed;
    }

    private static void RememberNewest(Account acc)
    {
        var procs = Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            var newest = procs.OrderByDescending(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } })
                .FirstOrDefault();
            if (newest != null) _lastProcess[acc.UserId] = newest.Id;
        }
        catch { }
        finally { foreach (var p in procs) p.Dispose(); }   // release process handles
    }

    private static void TryPatchFps(int fps)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "ClientSettings");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ClientAppSettings.json"),
                "{\"DFIntTaskSchedulerTargetFps\":" + fps + "}");
        }
        catch { }
    }
}
