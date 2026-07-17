namespace RobloxAccountManager.Models;

/// <summary>
/// A time-triggered action: launch or close an account/preset at a given time of day,
/// optionally auto-closing after a run duration. Evaluated once per minute by the scheduler.
/// </summary>
public class ScheduledTask
{
    public string Name { get; set; } = "New task";
    public bool Enabled { get; set; } = true;

    public ScheduleAction Action { get; set; } = ScheduleAction.Launch;

    /// <summary>Preset to run, or empty to use the single account below.</summary>
    public string PresetName { get; set; } = "";
    public string Alias { get; set; } = "";
    public long PlaceId { get; set; }

    /// <summary>Local time of day, "HH:mm".</summary>
    public string TimeOfDay { get; set; } = "14:00";

    /// <summary>Days the task runs on. Empty = every day.</summary>
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>Auto-close this launch after N minutes (0 = never).</summary>
    public int AutoCloseAfterMinutes { get; set; } = 0;

    // Runtime guard so a task fires at most once per matching minute.
    public DateTime LastFiredUtc { get; set; } = DateTime.MinValue;
}

public enum ScheduleAction { Launch, Close }
