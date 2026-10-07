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

    /// <summary>"9:5" and "09:05" both mean 09:05 — normalise what a hand-edited file might hold.</summary>
    public static string NormalizeTime(string? value)
        => TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? $"{t.Hours:00}:{t.Minutes:00}"
            : "";

    /// <summary>The next local time this task will run, or null when it never will (disabled or invalid).</summary>
    public static DateTime? NextRun(ScheduledTask task, DateTime? from = null)
    {
        if (!task.Enabled) return null;
        string hhmm = NormalizeTime(task.TimeOfDay);
        if (hhmm.Length == 0) return null;
        var now = from ?? DateTime.Now;
        var time = TimeSpan.Parse(hhmm, System.Globalization.CultureInfo.InvariantCulture);
        for (int day = 0; day <= 7; day++)
        {
            var candidate = now.Date.AddDays(day).Add(time);
            if (candidate <= now) continue;
            if (task.Days.Count > 0 && !task.Days.Contains(candidate.DayOfWeek)) continue;
            return candidate;
        }
        return null;
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
                if (!string.Equals(NormalizeTime(task.TimeOfDay), hhmm, StringComparison.Ordinal)) continue;
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
                // An explicit "close" task means every client of those accounts, no exclusions —
                // unlike the auto-close after a launch, which only reclaims what it started.
                CloseMatchingAccounts(task);
                return;
            }

            // ---- Launch ----
            if (LockService.IsLocked)
            {
                DiagnosticsService.Warn("scheduler", $"Skipped task '{task.Name}': the manager is locked");
                return;
            }

            // Every client this run starts carries the tag, and so does a client the watchdog relaunches
            // in place of one of them. The auto-close below closes exactly those: never a client the user
            // started by hand, before or after the task fired.
            string origin = $"schedule:{task.Name}:{Guid.NewGuid():N}";
            int launched = 0;

            if (!string.IsNullOrWhiteSpace(task.PresetName))
            {
                var preset = PresetService.Find(task.PresetName);
                if (preset == null)
                    DiagnosticsService.Warn("scheduler", $"Task '{task.Name}': preset '{task.PresetName}' no longer exists");
                else if (!PresetService.TryBeginRun(preset.Name))
                    // Started by hand or through the local API and still launching: not a second time.
                    DiagnosticsService.Warn("scheduler", $"Task '{task.Name}': preset '{preset.Name}' is already launching");
                else
                {
                    string runName = preset.Name;   // EndRun needs the name it began with, even after a rename
                    try
                    {
                        var r = await PresetService.LaunchAsync(preset, origin: origin);
                        launched = r.Launched;
                        if (r.Error != null || r.Failed > 0)
                            DiagnosticsService.Warn("scheduler", $"Task '{task.Name}': {r.Launched} launched, {r.Failed} failed{(r.Error != null ? " — " + r.Error : "")}");
                    }
                    finally { PresetService.EndRun(runName); }
                }
            }
            else if (!string.IsNullOrWhiteSpace(task.Alias))
            {
                var acc = _resolve?.Invoke(task.Alias);
                if (acc != null && task.PlaceId > 0)
                {
                    var result = await LauncherService.LaunchAsync(acc, new JoinTarget(task.PlaceId), origin: origin);
                    if (result.Success) launched++;
                    else DiagnosticsService.Warn("scheduler", $"Task '{task.Name}': {result.Message}");
                }
                else DiagnosticsService.Warn("scheduler", $"Task '{task.Name}': account or place is missing");
            }
            ToastService.Info(L.T("Toast.TaskRan.Title"), task.Name);

            // ---- Optional auto-close ----
            if (task.AutoCloseAfterMinutes > 0 && launched > 0)
                _ = AutoCloseLaterAsync(task, TimeSpan.FromMinutes(task.AutoCloseAfterMinutes), origin);
        }
        catch { /* a single bad task must not take down the scheduler */ }
    }

    private static async Task AutoCloseLaterAsync(ScheduledTask task, TimeSpan after, string origin)
    {
        try
        {
            await Task.Delay(after);

            // The schedule is over: a crashed client of this run is not rejoined any more, and a relaunch
            // already under way closes the client it starts.
            WatchdogService.EndOrigin(origin);
            int closed = CloseOrigin(origin);

            // A launch from this run that was still being attributed only shows up later (up to ~40 s
            // after it started), so look once more.
            await Task.Delay(TimeSpan.FromSeconds(60));
            closed += CloseOrigin(origin);

            if (closed > 0)
                DiagnosticsService.Log("scheduler", $"Auto-closed {closed} client(s) for task '{task.Name}'");
        }
        catch (Exception ex) { DiagnosticsService.Warn("scheduler", $"Auto-close for task '{task.Name}' failed", ex); }
    }

    /// <summary>Closes every live client tagged with <paramref name="origin"/>. Returns how many closed.</summary>
    private static int CloseOrigin(string origin)
    {
        int closed = 0;
        foreach (var t in ProcessRegistry.All.Where(t => t.Origin == origin).ToList())
        {
            try
            {
                if (InstanceControlService.Close(ProcessRegistry.TokenFor(t))) closed++;
            }
            catch (Exception ex) { DiagnosticsService.Warn("scheduler", $"Auto-close failed for pid {t.Pid}", ex); }
        }
        return closed;
    }

    /// <summary>
    /// Closes every live client belonging to the accounts targeted by an explicit close task.
    /// </summary>
    private static void CloseMatchingAccounts(ScheduledTask task)
    {
        var targetUserIds = ResolveTargetUserIds(task);
        if (targetUserIds.Count == 0) return;

        // A crashed client of these accounts waiting to be rejoined would come back after the close.
        foreach (long id in targetUserIds) WatchdogService.CancelRejoin(id);

        int closed = 0;
        foreach (var t in ProcessRegistry.All)
        {
            if (!targetUserIds.Contains(t.UserId)) continue;
            try
            {
                if (InstanceControlService.Close(ProcessRegistry.TokenFor(t))) closed++;
            }
            catch (Exception ex) { DiagnosticsService.Warn("scheduler", $"Auto-close failed for pid {t.Pid}", ex); }
        }

        if (closed > 0)
            DiagnosticsService.Log("scheduler", $"Auto-closed {closed} client(s) for task '{task.Name}'");
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
