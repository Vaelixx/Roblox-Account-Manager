using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Polls the process registry for crashed/closed Roblox clients. On exit it notifies
/// via Discord webhook and, when the account has AutoRejoin on, relaunches it into the
/// same place/server it was in.
/// </summary>
public static class WatchdogService
{
    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    private static Func<long, Account?>? _accountLookup;
    private static bool _hooked;

    // Crash-loop brake: at most MaxRejoins auto-rejoins per account inside RejoinWindow.
    // Without this an instantly-crashing game relaunches forever (launch → crash → launch …).
    private const int MaxRejoins = 3;
    private static readonly TimeSpan RejoinWindow = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<long, Queue<DateTime>> _rejoins = new();

    /// <summary>Claims one rejoin slot for the account; false when the crash-loop cap is hit.</summary>
    private static bool TryClaimRejoin(long userId)
    {
        lock (_rejoins)
        {
            if (!_rejoins.TryGetValue(userId, out var q)) _rejoins[userId] = q = new();
            var cutoff = DateTime.UtcNow - RejoinWindow;
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count >= MaxRejoins) return false;
            q.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    /// <summary>Wires the account lookup used for auto-rejoin. Call once at startup.</summary>
    public static void Init(Func<long, Account?> accountLookup)
    {
        _accountLookup = accountLookup;
        if (!_hooked) { ProcessRegistry.Exited += OnClientExited; _hooked = true; }
    }

    public static void Apply()
    {
        var s = SettingsService.Current;
        if (s.WatchdogEnabled) Start(Math.Max(5, s.WatchdogCheckSeconds));
        else Stop();
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

        var s = SettingsService.Current;
        if (!s.WatchdogEnabled) return;

        var acc = _accountLookup?.Invoke(t.UserId);

        if (s.NotifyOnCrash && WebhookService.Configured)
            WebhookService.Disconnected(t.Alias, acc?.ThumbnailUrl, t.PlaceId);

        if (s.ToastOnCrash)
            ToastService.Warning("Client closed", $"{t.Alias} closed or crashed (place {t.PlaceId}).");

        if (acc == null || !acc.AutoRejoin) return;

        if (!TryClaimRejoin(acc.UserId))
        {
            // Crash loop: give up instead of relaunching forever.
            int mins = (int)RejoinWindow.TotalMinutes;
            if (s.ToastOnCrash)
                ToastService.Warning("Auto-rejoin paused",
                    $"{t.Alias} crashed {MaxRejoins} times within {mins} min — not rejoining.");
            if (WebhookService.Configured)
                WebhookService.ReconnectFailed(t.Alias, acc.ThumbnailUrl, t.PlaceId,
                    $"crash loop: {MaxRejoins} rejoins in {mins} min, giving up");
            return;
        }

        // We treat this exit as a crash and are about to auto-rejoin: tell plugins first.
        try { PluginService.RaiseCrashed(acc, t.PlaceId, t.JobId); } catch { }
        _ = RejoinAsync(acc, t);
    }

    private static async Task RejoinAsync(Account acc, ProcessRegistry.Tracked t)
    {
        try
        {
            await Task.Delay(3000); // let the crashed process fully die first
            var result = await LauncherService.LaunchAsync(acc, t.PlaceId, t.JobId);
            if (WebhookService.Configured)
            {
                if (result.Success) WebhookService.Reconnected(acc, t.PlaceId, t.JobId);
                else WebhookService.ReconnectFailed(t.Alias, acc.ThumbnailUrl, t.PlaceId, result.Message);
            }
        }
        catch { }
    }
}
