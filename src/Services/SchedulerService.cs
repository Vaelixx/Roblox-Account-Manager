using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Evaluates <see cref="ScheduledTask"/>s once per minute. A task fires when the current local
/// time matches its <c>HH:mm</c> and day filter; a per-task guard stops it re-firing within the
/// same minute. Launch tasks run a preset or a single account; Close tasks kill matching clients.
/// Optional auto-close ends the launched clients after N minutes.
/// </summary>
public static class SchedulerService
{
    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    private static Func<string, Account?>? _resolve;

    /// <summary>Wires the alias/username → account resolver used by single-account tasks.</summary>
    public static void Init(Func<string, Account?> resolver) => _resolve = resolver;

    /// <summary>Starts the once-a-minute evaluation loop (idempotent).</summary>
    public static void Start()
    {
        lock (_gate)
        {
            if (_timer != null) return;
            // Single-shot, re-armed after every tick so we stay aligned to wall-clock
            // minute boundaries. A fixed 60 s period drifts over hours and can skip a minute.
            _timer = new System.Threading.Timer(_ => Tick(), null, DueToNextMinute(), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Time until shortly after the next wall-clock minute boundary.</summary>
    private static TimeSpan DueToNextMinute()
    {
        var now = DateTime.Now;
        int ms = 60_000 - (now.Second * 1000 + now.Millisecond) + 250;   // +250 ms: land safely past :00
        return TimeSpan.FromMilliseconds(ms);
    }

    public static void Stop()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }

    private static void Tick()
    {
        try
        {
            var now = DateTime.Now;
            var hhmm = now.ToString("HH:mm");

            // Snapshot: the settings UI can add/remove tasks while we enumerate. An
            // unhandled exception in a Timer callback would take down the whole process.
            foreach (var task in SettingsService.Current.ScheduledTasks.ToArray())
            {
                if (!task.Enabled) continue;
                if (task.TimeOfDay != hhmm) continue;
                if (task.Days.Count > 0 && !task.Days.Contains(now.DayOfWeek)) continue;

                // Fire at most once per matching minute.
                if ((DateTime.UtcNow - task.LastFiredUtc) < TimeSpan.FromSeconds(90)) continue;
                task.LastFiredUtc = DateTime.UtcNow;

                _ = FireAsync(task);
            }
        }
        catch { /* never let a bad tick kill the scheduler (or the process) */ }
        finally
        {
            // Re-arm aligned to the next minute; skipped if Stop() ran meanwhile.
            lock (_gate) _timer?.Change(DueToNextMinute(), Timeout.InfiniteTimeSpan);
        }
    }

    private static async Task FireAsync(ScheduledTask task)
    {
        try
        {
            if (task.Action == ScheduleAction.Close)
            {
                CloseMatching(task);
                return;
            }

            // ---- Launch ----
            var launchedPids = new List<int>();

            if (!string.IsNullOrWhiteSpace(task.PresetName))
            {
                var preset = PresetService.Find(task.PresetName);
                if (preset != null) await PresetService.LaunchAsync(preset);
            }
            else if (!string.IsNullOrWhiteSpace(task.Alias))
            {
                var acc = _resolve?.Invoke(task.Alias);
                if (acc != null)
                    await LauncherService.LaunchAsync(acc, task.PlaceId);
            }

            // ---- Optional auto-close ----
            if (task.AutoCloseAfterMinutes > 0)
            {
                _ = AutoCloseLaterAsync(task, TimeSpan.FromMinutes(task.AutoCloseAfterMinutes));
            }
        }
        catch { /* a single bad task must not take down the scheduler */ }
    }

    private static async Task AutoCloseLaterAsync(ScheduledTask task, TimeSpan after)
    {
        await Task.Delay(after);
        CloseMatching(task);
    }

    /// <summary>Kills tracked clients that belong to the task's target (preset aliases or single alias).</summary>
    private static void CloseMatching(ScheduledTask task)
    {
        var targetUserIds = ResolveTargetUserIds(task);
        if (targetUserIds.Count == 0) return;

        foreach (var t in ProcessRegistry.All)
        {
            if (!targetUserIds.Contains(t.UserId)) continue;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(t.Pid);
                p.Kill();
                ProcessRegistry.Forget(t.Pid);
            }
            catch { }
        }
    }

    private static HashSet<long> ResolveTargetUserIds(ScheduledTask task)
    {
        var ids = new HashSet<long>();

        if (!string.IsNullOrWhiteSpace(task.PresetName))
        {
            var preset = PresetService.Find(task.PresetName);
            if (preset != null)
                foreach (var alias in preset.Aliases)
                {
                    var acc = _resolve?.Invoke(alias);
                    if (acc != null) ids.Add(acc.UserId);
                }
        }
        else if (!string.IsNullOrWhiteSpace(task.Alias))
        {
            var acc = _resolve?.Invoke(task.Alias);
            if (acc != null) ids.Add(acc.UserId);
        }

        return ids;
    }
}
