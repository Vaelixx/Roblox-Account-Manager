using System.Diagnostics;

namespace RobloxAccountManager.Services;

/// <summary>
/// Periodically samples the working-set (RAM) of every tracked Roblox client. Publishes a
/// per-client snapshot for the UI and, when enabled, kills any client that exceeds the
/// configured per-process cap. Same Start/Stop/Apply shape as <see cref="WatchdogService"/>.
/// </summary>
public static class RamMonitorService
{
    /// <param name="PrivateMb">Memory the client has committed for itself. Unlike the working set it
    /// doesn't drop when the client is trimmed, so it is what memory-growth restarts compare.</param>
    public record Sample(int Pid, string Alias, long WorkingSetMb, long PrivateMb = 0);

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
        // Memory-growth restarts (WatchdogService) need the samples even with the monitor itself off.
        if (s.RamMonitorEnabled || s.RestartOnRamGrowth) Start(Math.Max(2, s.RamMonitorSeconds));
        else Stop();
        ApplyAutoTrim();
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
        lock (_gate)
        {
            _timer?.Dispose(); _timer = null;
            _trimTimer?.Dispose(); _trimTimer = null;   // auto-trim is meaningless without sampling
        }
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
            var token = ProcessRegistry.TokenFor(t);
            long mb, privateMb;
            try
            {
                using var p = Process.GetProcessById(t.Pid);
                if (!ProcessRegistry.IsTrackedProcess(p, token)) continue;   // pid reused by another process
                p.Refresh();
                mb = p.WorkingSet64 / (1024 * 1024);
                privateMb = p.PrivateMemorySize64 / (1024 * 1024);
            }
            catch { continue; } // process gone between registry read and here → skip

            snapshot.Add(new Sample(t.Pid, t.Alias, mb, privateMb));

            if (s.RamMonitorEnabled && s.AutoCloseOnHighRam && s.RamLimitMb > 0 && mb > s.RamLimitMb)
                TryKill(token, t.Alias, mb, s.RamLimitMb);
        }

        Latest = snapshot;
        try { Sampled?.Invoke(snapshot); } catch { }
    }

    /// <summary>Outcome of a trim pass: how many clients were trimmed and how much was released.</summary>
    public record TrimResult(int Clients, long FreedMb)
    {
        public string Summary => Clients == 0
            ? L.T("Ram.Trim.None")
            : FreedMb > 0
                ? L.N("Ram.Trim.Freed", Clients, FreedMb.ToString("N0"))
                : L.N("Ram.Trim.Nothing", Clients);
    }

    /// <summary>
    /// Pages every tracked client's idle memory out to the standby list.
    ///
    /// The pages return when the client touches them again, so this is not a permanent saving —
    /// it hands memory back that a client is holding but not using, which is what relieves a
    /// machine running several clients at once. Called manually, and on a timer when auto-trim
    /// is on.
    /// </summary>
    public static TrimResult TrimAll()
    {
        int trimmed = 0;
        long before = 0, after = 0;

        foreach (var t in ProcessRegistry.All)
        {
            try
            {
                var token = ProcessRegistry.TokenFor(t);
                using var p = Process.GetProcessById(token.Pid);
                if (!ProcessRegistry.IsTrackedProcess(p, token)) continue;
                p.Refresh();
                long b = p.WorkingSet64;

                if (!Win32.EmptyWorkingSet(p.Handle)) continue;

                p.Refresh();
                before += b;
                after += p.WorkingSet64;
                trimmed++;
            }
            catch { /* exited, or denied — skip and keep going */ }
        }

        // Clamp: the client keeps running while we measure, so a busy one can legitimately grow
        // during the pass and produce a negative delta. Reporting "-40 MB freed" reads as a bug.
        long freed = Math.Max(0, (before - after) / (1024 * 1024));

        if (trimmed > 0)
        {
            DiagnosticsService.Log("ram", $"Trimmed {trimmed} client(s), released {freed} MB");
            TickCore();   // refresh the readout so the UI shows the post-trim numbers
        }
        return new TrimResult(trimmed, freed);
    }

    // ---- auto-trim ----
    private static System.Threading.Timer? _trimTimer;

    /// <summary>Starts/stops the periodic trim to match settings. Safe to call repeatedly.</summary>
    public static void ApplyAutoTrim()
    {
        lock (_gate)
        {
            _trimTimer?.Dispose();
            _trimTimer = null;

            var s = SettingsService.Current;
            if (!s.RamMonitorEnabled || !s.AutoTrimEnabled) return;

            var period = TimeSpan.FromMinutes(Math.Clamp(s.AutoTrimMinutes, 5, 240));
            _trimTimer = new System.Threading.Timer(
                _ => { try { TrimAll(); } catch { } },   // a throwing timer callback kills the process
                null, period, period);
        }
    }

    private static void TryKill(ProcessRegistry.ProcessToken token, string alias, long mb, int limit)
    {
        try
        {
            using var p = Process.GetProcessById(token.Pid);
            if (!ProcessRegistry.IsTrackedProcess(p, token)) { ProcessRegistry.Prune(); return; }
            ProcessRegistry.MarkClosing(token);
            p.Kill();
            DiagnosticsService.Warn("ram", $"Closed {alias}: {mb} MB is over the {limit} MB limit");
            if (SettingsService.Current.EnableToasts)
                ToastService.Warning(L.T("Toast.RamKill.Title"), L.T("Toast.RamKill.Body", alias, mb, limit));
            if (WebhookService.Configured)
                WebhookService.Notify($"🧹 **{alias}** killed by RAM monitor ({mb} MB > {limit} MB).");
        }
        catch { }
    }
}
