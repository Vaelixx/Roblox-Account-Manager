using System.Diagnostics;

namespace RobloxAccountManager.Services;

/// <summary>
/// Periodically samples the working-set (RAM) of every tracked Roblox client. Publishes a
/// per-client snapshot for the UI and, when enabled, kills any client that exceeds the
/// configured per-process cap. Same Start/Stop/Apply shape as <see cref="WatchdogService"/>.
/// </summary>
public static class RamMonitorService
{
    public record Sample(int Pid, string Alias, long WorkingSetMb);

    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    // 0 = idle, 1 = a sample is in flight. Prevents a slow sample (many clients) from being
    // re-entered by the next timer fire on another pool thread, which would race on Latest.
    private static int _busy;

    /// <summary>Latest per-client RAM snapshot (empty until the first tick). Read-only for the UI.</summary>
    public static IReadOnlyList<Sample> Latest { get; private set; } = Array.Empty<Sample>();

    /// <summary>Raised (off the UI thread) after each sampling tick with the fresh snapshot.</summary>
    public static event Action<IReadOnlyList<Sample>>? Sampled;

    public static void Apply()
    {
        var s = SettingsService.Current;
        if (s.RamMonitorEnabled) Start(Math.Max(2, s.RamMonitorSeconds));
        else Stop();
    }

    private static void Start(int seconds)
    {
        lock (_gate)
        {
            var period = TimeSpan.FromSeconds(seconds);
            if (_timer == null)
                _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, period);
            else
                _timer.Change(TimeSpan.Zero, period);
        }
    }

    public static void Stop()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
        Latest = Array.Empty<Sample>();
    }

    private static void Tick()
    {
        // Skip if the previous sample is still running (slow enumeration under many clients).
        if (System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            TickCore();
        }
        finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }
    }

    private static void TickCore()
    {
        var s = SettingsService.Current;
        var snapshot = new List<Sample>();

        foreach (var t in ProcessRegistry.All)
        {
            long mb;
            try
            {
                using var p = Process.GetProcessById(t.Pid);
                p.Refresh();
                mb = p.WorkingSet64 / (1024 * 1024);
            }
            catch { continue; } // process gone between registry read and here → skip

            snapshot.Add(new Sample(t.Pid, t.Alias, mb));

            if (s.AutoCloseOnHighRam && s.RamLimitMb > 0 && mb > s.RamLimitMb)
                TryKill(t.Pid, t.Alias, mb, s.RamLimitMb);
        }

        Latest = snapshot;
        try { Sampled?.Invoke(snapshot); } catch { }
    }

    private static void TryKill(int pid, string alias, long mb, int limit)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            ProcessRegistry.Forget(pid);
            if (WebhookService.Configured)
                WebhookService.Notify($"🧹 **{alias}** killed by RAM monitor ({mb} MB > {limit} MB).");
        }
        catch { }
    }
}
