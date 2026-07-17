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
                _timer = new System.Threading.Timer(_ => ProcessRegistry.Prune(), null, period, period);
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

        var s = SettingsService.Current;
        if (!s.WatchdogEnabled) return;

        var acc = _accountLookup?.Invoke(t.UserId);

        if (s.NotifyOnCrash && WebhookService.Configured)
            WebhookService.Disconnected(t.Alias, acc?.ThumbnailUrl, t.PlaceId);

        if (s.ToastOnCrash)
            ToastService.Warning("Client closed", $"{t.Alias} closed or crashed (place {t.PlaceId}).");

        if (acc == null || !acc.AutoRejoin) return;

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
