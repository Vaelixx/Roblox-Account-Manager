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
    private static readonly ConcurrentDictionary<long, ProcessRegistry.ProcessToken> _lastProcess = new();

    // One launch at a time across the whole app (launch bar, presets, schedules, the watchdog, the local
    // API, plugins), from writing the shared graphics settings until the new client has been found.
    // Otherwise two clients starting together can be attributed to each other's accounts, and one
    // launch's profile flags can replace another's before its client has read them.
    private static readonly SemaphoreSlim _launchGate = new(1, 1);

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

        /// <summary>
        /// False when trying again cannot help (locked, no cookie, rejected cookie, no protocol handler) —
        /// the watchdog stops retrying a rejoin on those.
        /// </summary>
        public bool Retryable { get; init; } = true;

        /// <summary>Nothing was started because the launch was no longer wanted once its turn came (see <c>stillWanted</c>).</summary>
        public bool Skipped { get; init; }

        /// <summary>
        /// Completes with the client this launch produced once it has been found (PID plus start time,
        /// so a recycled PID can never be mistaken for it), or null when none appeared in time. The
        /// launch itself is fire-and-forget (Roblox's protocol handler starts the client), so this is
        /// the only signal that it actually came up.
        /// </summary>
        public Task<ProcessRegistry.ProcessToken?> Client { get; init; } = Task.FromResult<ProcessRegistry.ProcessToken?>(null);

        public static LaunchResult Ok(Task<ProcessRegistry.ProcessToken?> client) => new() { Success = true, Message = L.T("Launch.Launched"), Client = client };
        public static LaunchResult Fail(string m, bool retryable = true) => new() { Success = false, Message = m, Retryable = retryable };
    }

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

    /// <param name="profile">Performance profile for this client (<see cref="PerformanceProfiles"/>); null/empty = normal.</param>
    /// <param name="origin">Tag stored with the client (<see cref="ProcessRegistry.Tracked.Origin"/>); a relaunch passes it on.</param>
    public static Task<LaunchResult> LaunchAsync(Account acc, JoinTarget target, string? profile = null, string? origin = null)
        => LaunchAsync(acc, target, profile, origin, CancellationToken.None);

    /// <param name="ct">Gives up while the launch is still waiting for an earlier one to finish; a launch that has started runs to the end.</param>
    /// <param name="stillWanted">
    /// Asked once the launch's turn has come. False ends it without starting anything (<see cref="LaunchResult.Skipped"/>):
    /// a rejoin queued behind the user's own relaunch of that account is no longer needed then.
    /// </param>
    public static async Task<LaunchResult> LaunchAsync(Account acc, JoinTarget target, string? profile, string? origin, CancellationToken ct,
        Func<bool>? stillWanted = null)
    {
        if (LockService.IsLocked) return LaunchResult.Fail(L.T("Lock.Blocked"), retryable: false);
        if (string.IsNullOrEmpty(acc.Cookie)) return LaunchResult.Fail(L.T("Launch.NoCookie"), retryable: false);
        if (target.Kind != JoinKind.FollowUser && target.PlaceId <= 0) return LaunchResult.Fail(L.T("Launch.NeedPlace"), retryable: false);

        return await OneAtATimeAsync(() => StartClientAsync(acc, target, profile, origin), ct, stillWanted);
    }

    /// <summary>
    /// Runs a launch inside the launch gate. The gate stays taken until the client the launch started has
    /// been found or given up on (at most ~34 s), and is freed straight away when no client was started.
    /// </summary>
    private static async Task<LaunchResult> OneAtATimeAsync(Func<Task<LaunchResult>> launch, CancellationToken ct = default,
        Func<bool>? stillWanted = null)
    {
        await _launchGate.WaitAsync(ct);
        var client = Task.FromResult<ProcessRegistry.ProcessToken?>(null);
        try
        {
            // The manager may have been locked while this launch waited for the one before it.
            if (LockService.IsLocked) return LaunchResult.Fail(L.T("Lock.Blocked"), retryable: false);
            // Every launch before this one has registered its client by now, so this answer is reliable.
            if (stillWanted != null && !stillWanted()) return new LaunchResult { Retryable = false, Skipped = true };
            var result = await launch();
            client = result.Client;
            return result;
        }
        finally
        {
            if (client.IsCompleted) _launchGate.Release();
            else _ = client.ContinueWith(_ => _launchGate.Release(), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Frame-rate cap, FastFlags, the profile and this account's own overrides, written before the client
    /// starts: it reads them once, at startup. A launch without a profile puts back what an earlier profile
    /// launch changed.
    /// </summary>
    private static void ApplyGraphics(AppSettings settings, Account acc, string? profile)
    {
        try
        {
            bool hadUndo = settings.ProfileUndo != null || settings.FpsCapBeforeProfile != null;
            FFlagsService.ApplyForLaunch(settings, acc, profile);
            if (hadUndo || !string.IsNullOrEmpty(profile)) SettingsService.Save();
        }
        catch (Exception ex) { DiagnosticsService.Warn("launcher", "Could not apply graphics settings before launch", ex); }
    }

    private static async Task<LaunchResult> StartClientAsync(Account acc, JoinTarget target, string? profile, string? origin)
    {
        var settings = SettingsService.Current;
        EnsureMultiInstance(settings.EnableMultiInstance);
        ApplyGraphics(settings, acc, profile);

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
                return LaunchResult.Fail(L.T("Launch.CookieExpired"), retryable: false);
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

            // What presence said before this launch: a stale "in game" from the previous session must
            // not count as this client having loaded (see MinimizeWhenInGameAsync).
            string? gameBefore = acc.Presence == PresenceStatus.InGame ? acc.GameId ?? "" : null;
            var client = Task.Run(() => AttributeClientAsync(acc, target, profile, launchedAt, gameBefore, origin));

            // Off the launch's own path: a plugin that starts a launch from this event would otherwise wait
            // for the gate this launch holds, and every launch in the app would hang.
            long placeForPlugins = target.PlaceId;
            _ = Task.Run(() => { try { PluginService.RaiseLaunched(acc, placeForPlugins, jobId); } catch { } });
            if (settings.ToastOnLaunch)
                ToastService.Success(L.T("Toast.Launched.Title"), L.T("Toast.Launched.Body", acc.DisplayNameOrUser));
            if (settings.NotifyOnConnect && WebhookService.Configured)
                WebhookService.Connected(acc, target.PlaceId, jobId);
            AuditLogService.Log(AuditLogService.Category.Launch, $"Launched {acc.DisplayNameOrUser} into {target}");
            return LaunchResult.Ok(client);
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(ExplainLaunchFailure(ex), retryable: false);
        }
    }

    /// <summary>
    /// Binds the client this launch produced to its account. Retried: on a cold start (shader cache,
    /// a pending Roblox update, a slow disk) the client can take far longer than a few seconds to
    /// exist, and a single miss meant no Anti-AFK, crash watchdog or RAM cap for it.
    /// </summary>
    private static async Task<ProcessRegistry.ProcessToken?> AttributeClientAsync(Account acc, JoinTarget target, string? profile,
        DateTime launchedAt, string? gameBefore = null, string? origin = null)
    {
        await Task.Delay(4000);

        ProcessRegistry.ProcessToken? token = null;
        for (int attempt = 0; attempt < 15 && token == null; attempt++)
        {
            try
            {
                int pid = ProcessRegistry.RegisterNewest(acc, target, profile, launchedAt, origin);
                if (pid != 0 && ProcessRegistry.TryGetToken(pid, out var resolved)) token = resolved;
            }
            catch { }
            if (token == null) await Task.Delay(2000);
        }

        if (token is { } found)
        {
            _lastProcess[acc.UserId] = found;
            if (PerformanceProfiles.Minimizes(profile, SettingsService.Current.UltraLowAfk))
                _ = MinimizeWhenInGameAsync(acc, found, gameBefore);
        }
        else DiagnosticsService.Warn("launcher", $"No client could be attributed to {acc.DisplayNameOrUser} within 34s of launch");
        return token;
    }

    /// <summary>
    /// Minimizes a freshly launched client once it has joined its game. Roblox stops loading while
    /// its window is minimized, so minimizing on the splash screen left the client stuck until someone
    /// restored it. "Joined" is the account's presence turning In Game — a fresh value, not one left
    /// over from the session before — plus a short settle. Without that signal (presence switched
    /// off, or never reported within 5 minutes) the client is left as it is.
    /// </summary>
    /// <param name="gameBefore">Game id presence reported before the launch when it already said In Game; null otherwise.</param>
    private static async Task MinimizeWhenInGameAsync(Account acc, ProcessRegistry.ProcessToken client, string? gameBefore)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        DateTime? windowSince = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000);
            if (!ProcessRegistry.All.Any(t => ProcessRegistry.TokenFor(t) == client)) return;   // closed meanwhile
            if (ProcessRegistry.WindowHandle(client) == IntPtr.Zero) continue;                 // still starting
            windowSince ??= DateTime.UtcNow;

            if (acc.Presence != PresenceStatus.InGame) continue;
            // In game already before the launch and still the same server: presence may simply not
            // have caught up. Give loading a generous minute and a half instead of trusting it.
            bool fresh = gameBefore == null || (acc.GameId ?? "") != gameBefore;
            var settle = fresh ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(90);
            if (DateTime.UtcNow - windowSince.Value < settle) continue;

            InstanceControlService.Minimize(client);
            return;
        }
        DiagnosticsService.Log("launcher", $"Left {acc.DisplayNameOrUser}'s client open: it never showed as in game (minimizing earlier stops Roblox loading)");
    }

    /// <summary>Options for <see cref="LaunchBatchAsync"/>. <c>Origin</c> tags every client the batch starts.</summary>
    public sealed record BatchOptions(int DelaySeconds, int RandomDelaySeconds = 0, string? Profile = null, string? Origin = null);

    /// <param name="NotStarted">Accounts skipped because the batch was stopped.</param>
    public sealed record BatchResult(int Launched, int Failed, IReadOnlyList<string> Errors, int NotStarted = 0);

    /// <summary>
    /// Launches accounts one after another into the same target. Before the next account starts, the
    /// previous client has to have appeared (or the attribution window run out) AND the delay has to
    /// have passed — whichever takes longer. Waiting for the client keeps each launch's graphics
    /// settings from being overwritten before that client has read them, and stops two clients that
    /// start together from being attributed to each other (the launch gate guarantees that for every
    /// caller; waiting here as well lets the status line say so). A failed account never stops the
    /// batch or touches the ones that already launched.
    /// </summary>
    /// <param name="onLaunching">Account about to launch and its index (UI status).</param>
    /// <param name="onWaiting">Seconds left before the next launch; -1 while only waiting for the client.</param>
    /// <param name="ct">Stops the batch between accounts, also while it waits for a launch from elsewhere. Clients already launched keep running.</param>
    public static async Task<BatchResult> LaunchBatchAsync(IReadOnlyList<Account> accounts, JoinTarget target, BatchOptions options,
        Action<Account, int>? onLaunching = null, Action<int>? onWaiting = null, CancellationToken ct = default)
    {
        int launched = 0, attempted = 0;
        var errors = new List<string>();

        for (int i = 0; i < accounts.Count && !ct.IsCancellationRequested; i++)
        {
            var acc = accounts[i];
            onLaunching?.Invoke(acc, i);

            LaunchResult r;
            try { r = await LaunchAsync(acc, target, options.Profile, options.Origin, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }   // stopped before this account started
            catch (Exception ex)
            {
                DiagnosticsService.Warn("launcher", $"Launch of {acc.DisplayNameOrUser} threw", ex);
                r = LaunchResult.Fail(L.T("Launch.Failed", ex.GetType().Name));
            }
            attempted++;
            if (r.Success) launched++;
            else errors.Add($"{acc.DisplayNameOrUser}: {r.Message}");

            if (i == accounts.Count - 1) break;

            int delay = Math.Max(0, options.DelaySeconds);
            if (options.RandomDelaySeconds > 0) delay += Random.Shared.Next(options.RandomDelaySeconds + 1);

            var waitForClient = r.Success ? r.Client : Task.FromResult<ProcessRegistry.ProcessToken?>(null);
            try
            {
                for (int left = delay; left > 0; left--)
                {
                    onWaiting?.Invoke(left);
                    await Task.Delay(1000, ct);
                }
                if (!waitForClient.IsCompleted)
                {
                    onWaiting?.Invoke(-1);
                    await waitForClient.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) { break; }
        }

        return new BatchResult(launched, attempted - launched, errors, accounts.Count - attempted);
    }

    /// <summary>
    /// Turns a Process.Start failure on the roblox-player: URI into something actionable. The raw
    /// message ("The system cannot find the file specified") points at nothing; the cause is almost
    /// always a missing or hijacked protocol handler.
    /// </summary>
    /// <remarks>
    /// Never echoes <c>ex.Message</c>: Process.Start puts the file name in it, and here that is the
    /// roblox-player: URI carrying the auth ticket and any private-server code. The message goes to the
    /// status bar, toasts and the crash webhook.
    /// </remarks>
    private static string ExplainLaunchFailure(Exception ex)
    {
        if (ex is System.ComponentModel.Win32Exception or FileNotFoundException) return L.T("Launch.NoHandler");
        DiagnosticsService.Warn("launcher", $"Starting the Roblox client failed ({ex.GetType().Name})");
        return L.T("Launch.Failed", ex.GetType().Name);
    }

    /// <summary>Opens the Roblox app itself (home screen), signed in as this account — no game.</summary>
    public static async Task<LaunchResult> OpenRobloxAppAsync(Account acc)
    {
        if (LockService.IsLocked) return LaunchResult.Fail(L.T("Lock.Blocked"));
        return await OneAtATimeAsync(() => StartAppAsync(acc));
    }

    private static async Task<LaunchResult> StartAppAsync(Account acc)
    {
        var settings = SettingsService.Current;
        EnsureMultiInstance(settings.EnableMultiInstance);
        // No profile, like any plain launch: puts back the normal flags and frame-rate cap an earlier
        // profile launch (Ultra-low) left behind, and writes this account's own overrides.
        ApplyGraphics(settings, acc, null);
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
            var client = Task.Run(() => AttributeClientAsync(acc, new JoinTarget(0), null, launchedAt));
            if (SettingsService.Current.ToastOnLaunch)
                ToastService.Success(L.T("Toast.Launched.Title"), L.T("Toast.Launched.Body", acc.DisplayNameOrUser));
            return LaunchResult.Ok(client);
        }
        catch (Exception ex) { return LaunchResult.Fail(ExplainLaunchFailure(ex)); }
    }

    private static void CloseLast(Account acc)
    {
        if (!_lastProcess.TryGetValue(acc.UserId, out var token)) return;
        try
        {
            using var p = Process.GetProcessById(token.Pid);
            if (!p.HasExited && ProcessRegistry.IsTrackedProcess(p, token))
            {
                // A deliberate close, not a crash: flag it so the watchdog does not answer it with an
                // auto-rejoin (the session is still booked as playtime).
                ProcessRegistry.MarkClosing(token);
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
        // Nothing should come back on its own afterwards: a crashed client waiting to be rejoined included.
        // Every tracked client is flagged first, so a timed restart or disconnect check during the loop
        // leaves them alone, and rejoins are cancelled again after it for anything booked meanwhile.
        foreach (var t in ProcessRegistry.All) ProcessRegistry.MarkClosing(ProcessRegistry.TokenFor(t));
        WatchdogService.CancelAllRejoins();

        int closed = 0;
        foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try
            {
                if (!p.HasExited)
                {
                    // Deliberate close: flag it so the watchdog does not auto-rejoin it.
                    if (ProcessRegistry.TryGetToken(p.Id, out var token)
                        && ProcessRegistry.IsTrackedProcess(p, token))
                        ProcessRegistry.MarkClosing(token);
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1500)) p.Kill();
                    closed++;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        _lastProcess.Clear();
        WatchdogService.CancelAllRejoins();
        try { ProcessRegistry.Prune(); } catch { }
        return closed;
    }
}
