using System;
using System.Threading;
using System.Threading.Tasks;

namespace RobloxAccountManager.Services;

/// <summary>
/// Periodically refreshes account presence (Online / In Game / In Studio / Offline)
/// for the live status dashboard. Runs on a lightweight background timer that only
/// hits the presence endpoint — thumbnails and robux are left to the heavier
/// on-demand refresh. Overlapping ticks are skipped via <see cref="_busy"/>, and the
/// whole loop is a no-op while <c>ShowPresence</c> is disabled.
/// </summary>
public static class PresenceService
{
    private static AccountStore? _store;
    private static Timer? _timer;
    private static readonly object _gate = new();
    // 0 = idle, 1 = a poll is in flight. Interlocked so a manual PollNowAsync racing the
    // periodic tick can't both pass the guard and double up the network calls.
    private static int _busy;

    /// <summary>Raised after every completed poll (fires on a threadpool thread).</summary>
    public static event Action? PresenceUpdated;

    public static void Init(AccountStore store) => _store = store;

    /// <summary>(Re)starts the timer using the configured cadence. Idempotent.</summary>
    public static void Start()
    {
        if (_store == null) return;
        lock (_gate)   // serialise timer create/dispose so rapid restarts can't leak a Timer
        {
            Stop();
            if (!SettingsService.Current.ShowPresence) return;

            int secs = Math.Max(10, SettingsService.Current.PresencePollSeconds);
            _timer = new Timer(_ => _ = TickAsync(), null,
                               TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(secs));
        }
    }

    public static void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Forces an immediate poll regardless of the timer cadence.</summary>
    public static Task PollNowAsync() => TickAsync();

    private static async Task TickAsync()
    {
        if (_store == null) return;
        if (!SettingsService.Current.ShowPresence) return;
        // Atomic claim: bail if a poll is already running (periodic tick vs. PollNowAsync).
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

        try
        {
            await _store.RefreshPresenceOnlyAsync();
            PresenceUpdated?.Invoke();
        }
        catch { /* transient network failure — retry on the next tick */ }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
