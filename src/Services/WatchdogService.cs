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

    /// <summary>
    /// Cancels a waiting rejoin and resets the account's streak (the user stepped in, or the account's
    /// clients are being closed on purpose). A relaunch already under way closes the client it started.
    /// </summary>
    public static void CancelRejoin(long userId)
    {
        bool cancelled;
        lock (_rejoinGate)
        {
            cancelled = _pending.Remove(userId, out var p);
            if (cancelled) { try { p.Cts.Cancel(); } catch (ObjectDisposedException) { } }
            _streak.Remove(userId);
        }
        if (cancelled) DiagnosticsService.Log("watchdog", $"Waiting rejoin cancelled for user {userId}");
    }

    /// <summary>Cancels every waiting rejoin: after "close all" nothing should come back on its own.</summary>
    public static void CancelAllRejoins()
    {
        lock (_rejoinGate)
        {
            foreach (var p in _pending.Values) { try { p.Cts.Cancel(); } catch (ObjectDisposedException) { } }
            _pending.Clear();
            _streak.Clear();
        }
    }

    // Origins whose schedule has run its auto-close. Their clients are not rejoined or restarted any more,
    // and a relaunch that was already under way closes the client it started.
    private static readonly ConcurrentDictionary<string, byte> _endedOrigins = new();

    /// <summary>
    /// Ends the run of a schedule (see <see cref="ProcessRegistry.Tracked.Origin"/>) once its auto-close
    /// has fired: waiting rejoins of its clients are cancelled, and none is booked for it afterwards.
    /// </summary>
    public static void EndOrigin(string origin)
    {
        _endedOrigins[origin] = 0;
        lock (_rejoinGate)
        {
            foreach (var userId in _pending.Where(kv => kv.Value.Origin == origin).Select(kv => kv.Key).ToList())
            {
                if (_pending.Remove(userId, out var p)) { try { p.Cts.Cancel(); } catch (ObjectDisposedException) { } }
                _streak.Remove(userId);
            }
        }
    }

    private static bool IsEnded(string? origin) => origin != null && _endedOrigins.ContainsKey(origin);

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
    /// <param name="countAsRejoin">False for a timed restart: the overview's rejoin count is about recoveries.</param>
    private static (int Streak, TimeSpan Delay, CancellationTokenSource Cts)? BookRejoin(long userId, TimeSpan previousUptime, string? origin,
        bool countAsRejoin = true)
    {
        if (IsEnded(origin)) return null;   // the schedule that started the client is over
        lock (_rejoinGate)
        {
            if (_pending.ContainsKey(userId)) return null;
            int streak = RejoinBackoff.StreakAfterExit(_streak.GetValueOrDefault(userId), previousUptime);
            var delay = RejoinBackoff.DelayFor(streak);
            _streak[userId] = streak + 1;
            if (countAsRejoin) _sessionRejoins[userId] = _sessionRejoins.GetValueOrDefault(userId) + 1;
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

    /// <summary>Stops the exit check and timed restarts (<see cref="Apply"/> starts what is switched on again).</summary>
    public static void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _restartTimer?.Dispose();
            _restartTimer = null;
        }
    }

    private static void OnClientExited(ProcessRegistry.Tracked t)
    {
        // Fires for every tracked-client exit, independent of the watchdog toggle
        // (the Exited hook is wired once in Init). Plugins learn the client is gone.
        try { PluginService.RaiseClosed(t.UserId, t.Alias); } catch { }

        // A long session ends any crash streak however it ended, so a later quick crash is rejoined at
        // once rather than after the wait an old streak had built up.
        if (t.UserId > 0 && t.Uptime >= RejoinBackoff.StableAfter)
            lock (_rejoinGate) _streak.Remove(t.UserId);

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

    /// <param name="timedRestart">A planned restart, not a recovery: no "reconnected" post for it.</param>
    private static async Task RejoinAsync(Account acc, ProcessRegistry.Tracked t, JoinTarget target, TimeSpan delay, CancellationTokenSource cts,
        bool timedRestart = false)
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
                // The launch may wait its turn behind others; by then the user may have relaunched the
                // account themselves, or stopped this rejoin.
                try
                {
                    result = await LauncherService.LaunchAsync(acc, target, t.Profile, t.Origin, cts.Token,
                        stillWanted: () => !cts.IsCancellationRequested && !IsEnded(t.Origin) && !HasLiveClient(acc.UserId));
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                if (result.Skipped) return;
                if (result.Success && (cts.IsCancellationRequested || IsEnded(t.Origin)))
                {
                    // Stopped (Stop, close all, the schedule's end) while that launch was under way: the
                    // client it started must not stay.
                    if (await result.Client is { } started) await Task.Run(() => InstanceControlService.Close(started));
                    return;
                }
                if (result.Success || !result.Retryable || attempt >= RetryDelays.Length) break;
                DiagnosticsService.Warn("watchdog", $"Rejoin of {t.Alias} failed, retrying: {result.Message}");
                try { await Task.Delay(RetryDelays[attempt], cts.Token); }
                catch (OperationCanceledException) { return; }
            }

            bool ok = result.Success;
            if (WebhookService.Configured)
            {
                if (ok) { if (!timedRestart) WebhookService.Reconnected(acc, target.PlaceId, target.JobId); }
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

    // Per client, keyed by process identity: a new client that reuses a PID starts with a clean record.
    private static readonly ConcurrentDictionary<ProcessRegistry.ProcessToken, DisconnectTrack> _tracks = new();

    /// <summary>Per account, across its clients.</summary>
    private sealed class AccountWatch
    {
        /// <summary>Presence has shown the account in a game this session: proof that it can see the account at all.</summary>
        public bool SeenInGame;

        /// <summary>Disconnect restarts since one of its clients was last in a game.</summary>
        public int RestartsSinceLoaded;

        /// <summary>Restarts stopped because none of those clients got into a game (the user has been told).</summary>
        public bool GaveUp;
    }

    private static readonly ConcurrentDictionary<long, AccountWatch> _accounts = new();

    /// <summary>
    /// Runs after every presence poll Roblox answered. A client whose account was in game and has not been
    /// for <see cref="AppSettings.DisconnectMinutes"/> — or that never got into a game within a few minutes —
    /// is closed and rejoined like a crash (same retries, same backoff). Only what Roblox reported in this poll
    /// counts; "never got into a game" only counts for an account presence has shown in a game before; and
    /// after <see cref="DisconnectTrack.MaxUnloadedRestarts"/> restarts in a row whose client never got in,
    /// the account is left alone until it is in a game again. Accounts with more than one client are
    /// skipped: presence can't tell which of them dropped.
    /// </summary>
    private static void CheckDisconnects()
    {
        try
        {
            var s = SettingsService.Current;
            if (!s.WatchdogEnabled || !s.RejoinOnDisconnect || !s.ShowPresence) { _tracks.Clear(); return; }

            var clients = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).ToList();
            var live = clients.Select(ProcessRegistry.TokenFor).ToHashSet();
            foreach (var gone in _tracks.Keys.Where(k => !live.Contains(k)).ToList()) _tracks.TryRemove(gone, out _);

            var now = DateTime.UtcNow;
            var limit = TimeSpan.FromMinutes(s.DisconnectMinutes);
            var poll = PresenceService.LastFetchedUtc;
            // Locked, nothing could be relaunched: keep watching, but leave every client as it is.
            bool locked = LockService.IsLocked;
            foreach (var group in clients.GroupBy(t => t.UserId).Where(g => g.Count() == 1))
            {
                var t = group.First();
                if (t.ClosingIntentionally || (t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0)) continue;   // home screen
                var acc = _accountLookup?.Invoke(t.UserId);
                if (acc == null || !acc.AutoRejoin) continue;
                // Not in this poll's answer: what the account shows is its last known state, possibly from
                // before this client even started.
                if (acc.PresenceFetchedUtc != poll) continue;

                bool inGame = acc.Presence == PresenceStatus.InGame;
                var watch = _accounts.GetOrAdd(t.UserId, _ => new AccountWatch());
                if (inGame) watch.SeenInGame = true;

                var track = _tracks.GetOrAdd(ProcessRegistry.TokenFor(t), _ => new DisconnectTrack(now, inGame, acc.GameId));
                switch (track.Observe(inGame, acc.GameId, now, limit, watch.SeenInGame, watch.RestartsSinceLoaded))
                {
                    case DisconnectVerdict.Loaded:
                        watch.RestartsSinceLoaded = 0;
                        watch.GaveUp = false;
                        break;
                    case DisconnectVerdict.GiveUp:
                        track.Handled = true;
                        if (!watch.GaveUp) { watch.GaveUp = true; AnnounceGiveUp(t.Alias, watch.RestartsSinceLoaded); }
                        break;
                    case DisconnectVerdict.Disconnected or DisconnectVerdict.NeverLoaded when !locked:
                        if (RecoverDisconnected(t, acc)) { track.Handled = true; watch.RestartsSinceLoaded++; }
                        break;
                }
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Disconnect check failed", ex); }
    }

    /// <summary>Closes a client that looks disconnected and rejoins it like a crash. False when no rejoin could be booked.</summary>
    private static bool RecoverDisconnected(ProcessRegistry.Tracked t, Account acc)
    {
        var booking = BookRejoin(acc.UserId, t.Uptime, t.Origin);
        if (booking is not { } b) return false;
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
        return true;
    }

    /// <summary>Tells the user (once) that the watchdog stopped restarting an account whose new clients never get into a game.</summary>
    private static void AnnounceGiveUp(string alias, int restarts)
    {
        DiagnosticsService.Warn("watchdog", $"{alias} didn't get into a game after {restarts} restarts in a row; no more disconnect restarts until it is in a game again");
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Watchdog.Disconnect.Title"), L.T("Watchdog.DisconnectGaveUp", alias, restarts));
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
            // The longest-running client that can be relaunched: one whose account is gone or has no
            // session must not hold up the others.
            var (due, acc) = ProcessRegistry.All
                .Where(t => !t.IsExternal && t.UserId > 0 && !t.ClosingIntentionally && t.Uptime >= limit
                            && !(t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0) && !IsEnded(t.Origin))
                .OrderByDescending(t => t.Uptime)
                .Select(t => (Client: t, Account: _accountLookup?.Invoke(t.UserId)))
                .FirstOrDefault(x => x.Account is { } a && !string.IsNullOrEmpty(a.Cookie));
            if (due == null || acc == null) return;

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

            // Booked like a crash rejoin: a failed relaunch is retried, the wait shows on the overview with
            // Stop, and nothing happens while a rejoin for the account is already on its way. The client
            // ran long enough to start a fresh streak, so the relaunch follows within seconds.
            var booking = BookRejoin(acc.UserId, t.Uptime, t.Origin, countAsRejoin: false);
            if (booking is not { } b) return;
            DiagnosticsService.Log("watchdog", $"Timed restart of {t.Alias} after {(int)t.Uptime.TotalMinutes} min into {target}");

            var token = ProcessRegistry.TokenFor(t);
            ProcessRegistry.MarkClosing(token);   // deliberate: no crash handling
            await Task.Run(() => InstanceControlService.Close(token));
            await RejoinAsync(acc, t, target, b.Delay, b.Cts, timedRestart: true);
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", $"Timed restart of {t.Alias} failed", ex); }
        finally { Interlocked.Exchange(ref _restarting, 0); }
    }
}
