using System.Collections.Concurrent;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Polls the process registry for crashed/closed Roblox clients. On exit it notifies
/// via Discord webhook and, when the account has AutoRejoin on, relaunches it into the
/// same destination it was launched into — the same private server, the same followed player.
///
/// Recovery never gives up while rejoin is on, but it is paced: a failed relaunch is retried a
/// couple of times, a public server that keeps failing is swapped for any server of the same place,
/// and after a few rejoins in a row each further one waits longer (<see cref="RejoinBackoff"/>), so
/// a game that crashes instantly is retried a few times an hour rather than in a tight loop.
///
/// Two optional extras run through the same rejoin path: a client whose account stops showing as
/// in game (disconnected, but the process is still open) is restarted, and every client can be
/// restarted after a set time.
/// </summary>
public static class WatchdogService
{
    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    private static Func<long, Account?>? _accountLookup;
    private static bool _hooked;

    // Crash-loop protection by backing off, never by giving up (see RejoinBackoff): per account, how
    // many rejoins happened in a row, and the rejoin currently waiting out its delay.
    private static readonly object _rejoinGate = new();
    private static readonly Dictionary<long, int> _streak = new();
    private static readonly Dictionary<long, (DateTime DueUtc, CancellationTokenSource Cts, string? Origin)> _pending = new();

    // Relaunch attempts per rejoin when the launch itself fails (network, Roblox API hiccup).
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60) };

    private static readonly Dictionary<long, int> _sessionRejoins = new();

    /// <summary>Rejoins waiting out a backoff delay of a minute or more (the overview lists them with a Stop button).</summary>
    public static IReadOnlyList<(long UserId, DateTime DueUtc)> PendingRejoins()
    {
        lock (_rejoinGate)
            return _pending.Where(kv => kv.Value.DueUtc - DateTime.UtcNow > TimeSpan.FromSeconds(30))
                           .Select(kv => (kv.Key, kv.Value.DueUtc)).ToList();
    }

    /// <summary>Cancels a waiting rejoin and resets the account's streak (the user stepped in).</summary>
    public static void CancelRejoin(long userId)
    {
        lock (_rejoinGate)
        {
            if (_pending.Remove(userId, out var p)) { try { p.Cts.Cancel(); } catch (ObjectDisposedException) { } }
            _streak.Remove(userId);
        }
        DiagnosticsService.Log("watchdog", $"Waiting rejoin cancelled for user {userId}");
    }

    /// <summary>
    /// Cancels the waiting rejoins of clients a schedule started (see <see cref="ProcessRegistry.Tracked.Origin"/>):
    /// once the schedule's auto-close has run, a client of it that crashed must not come back.
    /// </summary>
    public static void CancelRejoins(string origin)
    {
        lock (_rejoinGate)
        {
            foreach (var userId in _pending.Where(kv => kv.Value.Origin == origin).Select(kv => kv.Key).ToList())
            {
                if (_pending.Remove(userId, out var p)) { try { p.Cts.Cancel(); } catch (ObjectDisposedException) { } }
                _streak.Remove(userId);
            }
        }
    }

    /// <summary>Auto-rejoins for an account since the manager started, for the dashboard.</summary>
    public static int RejoinsFor(long userId)
    {
        lock (_rejoinGate) return _sessionRejoins.GetValueOrDefault(userId);
    }

    /// <summary>
    /// Books one rejoin for the account: returns its place in the streak (1 = first in a row) and how
    /// long to wait before it, plus a token that <see cref="CancelRejoin"/> trips. Null when a rejoin
    /// for this account is already waiting.
    /// </summary>
    private static (int Streak, TimeSpan Delay, CancellationTokenSource Cts)? BookRejoin(long userId, TimeSpan previousUptime, string? origin)
    {
        lock (_rejoinGate)
        {
            if (_pending.ContainsKey(userId)) return null;
            int streak = RejoinBackoff.StreakAfterExit(_streak.GetValueOrDefault(userId), previousUptime);
            var delay = RejoinBackoff.DelayFor(streak);
            _streak[userId] = streak + 1;
            _sessionRejoins[userId] = _sessionRejoins.GetValueOrDefault(userId) + 1;
            var cts = new CancellationTokenSource();
            _pending[userId] = (DateTime.UtcNow + delay, cts, origin);
            return (streak + 1, delay, cts);
        }
    }

    /// <summary>Wires the account lookup used for auto-rejoin. Call once at startup.</summary>
    public static void Init(Func<long, Account?> accountLookup)
    {
        _accountLookup = accountLookup;
        if (!_hooked)
        {
            ProcessRegistry.Exited += OnClientExited;
            PresenceService.PresenceUpdated += CheckDisconnects;
            _hooked = true;
        }
    }

    public static void Apply()
    {
        var s = SettingsService.Current;
        if (s.WatchdogEnabled) Start(Math.Max(5, s.WatchdogCheckSeconds));
        else Stop();

        lock (_gate)
        {
            if (s.RestartClientsEnabled)
                _restartTimer ??= new System.Threading.Timer(_ => RestartTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            else { _restartTimer?.Dispose(); _restartTimer = null; }
        }
    }

    private static void Start(int seconds)
    {
        lock (_gate)
        {
            var period = TimeSpan.FromSeconds(seconds);
            if (_timer == null)
                _timer = new System.Threading.Timer(
                    _ => { try { ProcessRegistry.Prune(); } catch { } },   // a throwing Timer callback kills the process
                    null, period, period);
            else
                _timer.Change(period, period);
        }
    }

    public static void Stop()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }

    private static void OnClientExited(ProcessRegistry.Tracked t)
    {
        // Fires for every tracked-client exit, independent of the watchdog toggle
        // (the Exited hook is wired once in Init). Plugins learn the client is gone.
        try { PluginService.RaiseClosed(t.UserId, t.Alias); } catch { }

        // Adopted clients (started from the website / home screen) have no account behind them:
        // there is no cookie to rejoin with and no alias worth alerting about, so stay quiet
        // rather than posting "External client crashed (place 0)" to Discord.
        if (t.IsExternal || t.UserId == 0) return;

        // Closed on purpose through the manager (close button, "close all", relaunch, scheduler):
        // not a crash, nothing to report and nothing to rejoin.
        if (t.ClosingIntentionally) return;

        var s = SettingsService.Current;
        if (!s.WatchdogEnabled) return;

        var acc = _accountLookup?.Invoke(t.UserId);

        if (s.NotifyOnCrash && WebhookService.Configured)
            WebhookService.Disconnected(t.Alias, acc?.ThumbnailUrl, t.PlaceId);

        if (s.ToastOnCrash)
            ToastService.Warning(L.T("Toast.ClientClosed.Title"), L.T("Toast.ClientClosed.Body", t.Alias));

        if (acc == null || !acc.AutoRejoin) return;

        // Opened on the Roblox home screen ("open app"), not in a game: there is nothing to rejoin.
        if (t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0) return;

        var booking = BookRejoin(acc.UserId, t.Uptime, t.Origin);
        if (booking is not { } b) return;   // a rejoin for this account is already on its way
        AnnounceBackoff(t.Alias, acc, t.PlaceId, b.Streak, b.Delay);

        // We treat this exit as a crash and are about to auto-rejoin: tell plugins first.
        try { PluginService.RaiseCrashed(acc, t.PlaceId, t.JobId); } catch { }
        _ = RejoinAsync(acc, t, t.Target.ForRejoin(b.Streak), b.Delay, b.Cts);
    }

    /// <summary>A running client for the account that isn't one we are closing ourselves (pruned first, so a dead one never counts).</summary>
    private static bool HasLiveClient(long userId)
        => ProcessRegistry.ForUser(userId).Any(t => !t.IsExternal && !t.ClosingIntentionally);

    /// <summary>Tells the user when a rejoin is being slowed down (the streak went past the free rejoins).</summary>
    private static void AnnounceBackoff(string alias, Account acc, long placeId, int streak, TimeSpan delay)
    {
        if (delay < TimeSpan.FromMinutes(1)) return;
        int mins = (int)Math.Round(delay.TotalMinutes);
        DiagnosticsService.Warn("watchdog", $"{alias} needed {streak - 1} rejoins in a row; waiting {mins} min before the next");
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Toast.RejoinPaused.Title"), L.T("Toast.RejoinPaused.Body", alias, streak - 1, mins));
        if (WebhookService.Configured)
            WebhookService.ReconnectFailed(alias, acc.ThumbnailUrl, placeId, $"{streak - 1} rejoins in a row, next try in {mins} min");
    }

    private static async Task RejoinAsync(Account acc, ProcessRegistry.Tracked t, JoinTarget target, TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            try { await Task.Delay(delay, cts.Token); }   // at least a few seconds: let the old process fully die
            catch (OperationCanceledException) { return; }
            // Launched by hand (or by a schedule) while we waited: nothing left to recover.
            if (HasLiveClient(acc.UserId)) return;
            DiagnosticsService.Log("watchdog", $"Rejoining {t.Alias} into {target}");

            LauncherService.LaunchResult result;
            for (int attempt = 0; ; attempt++)
            {
                // Stopped from the overview, or the schedule that started the client has ended.
                if (LockService.IsLocked || cts.IsCancellationRequested) return;
                // Relaunched by hand (or by a schedule) while we waited to retry: nothing left to recover.
                if (attempt > 0 && HasLiveClient(acc.UserId)) return;
                result = await LauncherService.LaunchAsync(acc, target, t.Profile, t.Origin);
                if (result.Success || !result.Retryable || attempt >= RetryDelays.Length) break;
                DiagnosticsService.Warn("watchdog", $"Rejoin of {t.Alias} failed, retrying: {result.Message}");
                try { await Task.Delay(RetryDelays[attempt], cts.Token); }
                catch (OperationCanceledException) { return; }
            }

            bool ok = result.Success;
            if (WebhookService.Configured)
            {
                if (ok) WebhookService.Reconnected(acc, target.PlaceId, target.JobId);
                else WebhookService.ReconnectFailed(t.Alias, acc.ThumbnailUrl, target.PlaceId, result.Message);
            }
            if (!ok && SettingsService.Current.ToastOnCrash)
                ToastService.Warning(L.T("Toast.ClientClosed.Title"), $"{t.Alias}: {result.Message}");
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", $"Rejoin of {t.Alias} failed", ex); }
        finally
        {
            lock (_rejoinGate)
                if (_pending.TryGetValue(acc.UserId, out var p) && p.Cts == cts) _pending.Remove(acc.UserId);
            cts.Dispose();
        }
    }

    // ---------------------------------------------------------------- disconnects

    /// <summary>Per client: when its account was last seen in game, and when the manager first saw the client.</summary>
    private sealed class PresenceTrack
    {
        public DateTime FirstSeenUtc = DateTime.UtcNow;
        public DateTime? LastInGameUtc;
        public bool Handled;
    }

    private static readonly ConcurrentDictionary<int, PresenceTrack> _presence = new();

    /// <summary>
    /// Runs after every presence poll. A client whose account was in game and has not been for
    /// <see cref="AppSettings.DisconnectMinutes"/> — or that never got into a game within a few
    /// minutes — is closed and rejoined like a crash (same retries, same crash-loop cap). Accounts
    /// with more than one client are skipped: presence can't tell which of them dropped.
    /// </summary>
    private static void CheckDisconnects()
    {
        try
        {
            var s = SettingsService.Current;
            if (!s.WatchdogEnabled || !s.RejoinOnDisconnect || !s.ShowPresence) { _presence.Clear(); return; }

            var clients = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).ToList();
            foreach (int pid in _presence.Keys.Except(clients.Select(t => t.Pid)).ToList()) _presence.TryRemove(pid, out _);

            var now = DateTime.UtcNow;
            var limit = TimeSpan.FromMinutes(s.DisconnectMinutes);
            foreach (var group in clients.GroupBy(t => t.UserId).Where(g => g.Count() == 1))
            {
                var t = group.First();
                if (t.ClosingIntentionally || (t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0)) continue;   // home screen
                var acc = _accountLookup?.Invoke(t.UserId);
                if (acc == null || !acc.AutoRejoin) continue;

                var st = _presence.GetOrAdd(t.Pid, _ => new PresenceTrack());
                if (acc.Presence == PresenceStatus.InGame) { st.LastInGameUtc = now; st.Handled = false; continue; }
                if (st.Handled) continue;

                // Never in game yet: give loading (and a slow presence update) at least five minutes.
                var since = st.LastInGameUtc ?? st.FirstSeenUtc;
                var wait = st.LastInGameUtc == null ? TimeSpan.FromMinutes(Math.Max(5, s.DisconnectMinutes)) : limit;
                if (now - since < wait) continue;

                st.Handled = true;
                RecoverDisconnected(t, acc);
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Disconnect check failed", ex); }
    }

    private static void RecoverDisconnected(ProcessRegistry.Tracked t, Account acc)
    {
        var booking = BookRejoin(acc.UserId, t.Uptime, t.Origin);
        if (booking is not { } b) return;
        AnnounceBackoff(t.Alias, acc, t.PlaceId, b.Streak, b.Delay);

        DiagnosticsService.Warn("watchdog", $"{t.Alias} has not been in game for a while (disconnected?); restarting its client");
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Toast.ClientClosed.Title"), L.T("Watchdog.Disconnected", t.Alias));

        var token = ProcessRegistry.TokenFor(t);
        ProcessRegistry.MarkClosing(token);   // our own close: the exit must not trigger a second rejoin
        _ = Task.Run(async () =>
        {
            try { InstanceControlService.Close(token); } catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Closing a disconnected client failed", ex); }
            await RejoinAsync(acc, t, t.Target.ForRejoin(b.Streak), b.Delay, b.Cts);
        });
    }

    // ---------------------------------------------------------------- timed restart

    private static System.Threading.Timer? _restartTimer;
    private static DateTime _lastRestartUtc = DateTime.MinValue;
    private static int _restarting;

    /// <summary>
    /// Restarts the longest-running client once it has been up for <see cref="AppSettings.RestartClientsMinutes"/>.
    /// One client per minute at most, so a batch launched together isn't restarted all at once, and
    /// never while a restart is still in progress or the manager is locked (it couldn't relaunch).
    /// </summary>
    private static void RestartTick()
    {
        try
        {
            var s = SettingsService.Current;
            if (!s.RestartClientsEnabled || LockService.IsLocked) return;
            if (DateTime.UtcNow - _lastRestartUtc < TimeSpan.FromMinutes(1)) return;
            if (Volatile.Read(ref _restarting) != 0) return;

            var limit = TimeSpan.FromMinutes(s.RestartClientsMinutes);
            var due = ProcessRegistry.All
                .Where(t => !t.IsExternal && t.UserId > 0 && !t.ClosingIntentionally && t.Uptime >= limit
                            && !(t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0))
                .OrderByDescending(t => t.Uptime)
                .FirstOrDefault();
            if (due == null) return;

            var acc = _accountLookup?.Invoke(due.UserId);
            if (acc == null || string.IsNullOrEmpty(acc.Cookie)) return;

            _lastRestartUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _restarting, 1);
            _ = RestartAsync(acc, due);
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Timed restart check failed", ex); }
    }

    private static async Task RestartAsync(Account acc, ProcessRegistry.Tracked t)
    {
        try
        {
            // A long-lived public server may be gone by now: go back to the place, not that server.
            var target = t.Target.Kind == JoinKind.Server ? t.Target.WithoutServer() : t.Target;
            DiagnosticsService.Log("watchdog", $"Timed restart of {t.Alias} after {(int)t.Uptime.TotalMinutes} min into {target}");

            var token = ProcessRegistry.TokenFor(t);
            ProcessRegistry.MarkClosing(token);   // deliberate: no crash handling
            await Task.Run(() => InstanceControlService.Close(token));
            await Task.Delay(3000);

            var result = await LauncherService.LaunchAsync(acc, target, t.Profile, t.Origin);
            if (!result.Success)
            {
                DiagnosticsService.Warn("watchdog", $"Timed restart of {t.Alias} could not relaunch: {result.Message}");
                if (SettingsService.Current.ToastOnCrash)
                    ToastService.Warning(L.T("Toast.ClientClosed.Title"), $"{t.Alias}: {result.Message}");
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", $"Timed restart of {t.Alias} failed", ex); }
        finally { Interlocked.Exchange(ref _restarting, 0); }
    }
}
