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
    private static volatile bool _busy;

    /// <summary>Raised after every completed poll (fires on a threadpool thread).</summary>
    public static event Action? PresenceUpdated;

    public static void Init(AccountStore store) => _store = store;

    /// <summary>(Re)starts the timer using the configured cadence. Idempotent.</summary>
    public static void Start()
    {
        if (_store == null) return;
        Stop();
        if (!SettingsService.Current.ShowPresence) return;

        int secs = Math.Max(10, SettingsService.Current.PresencePollSeconds);
        _timer = new Timer(_ => _ = TickAsync(), null,
                           TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(secs));
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Forces an immediate poll regardless of the timer cadence.</summary>
    public static Task PollNowAsync() => TickAsync();

    private static async Task TickAsync()
    {
        if (_busy || _store == null) return;
        if (!SettingsService.Current.ShowPresence) return;

        _busy = true;
        try
        {
            await _store.RefreshPresenceOnlyAsync();
            PresenceUpdated?.Invoke();
        }
        catch { /* transient network failure — retry on the next tick */ }
        finally { _busy = false; }
    }
}
