using System.Collections.Concurrent;
using System.Diagnostics;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Tracks the RobloxPlayerBeta processes this app launched, keyed by PID, with the
/// account + place/job they belong to. Foundation for Anti-AFK, the crash watchdog,
/// the RAM monitor and the window-grid manager.
/// </summary>
public static class ProcessRegistry
{
    public class Tracked
    {
        public long UserId { get; init; }
        public string Alias { get; init; } = "";
        public string Cookie { get; init; } = "";
        public int Pid { get; set; }
        public long PlaceId { get; init; }
        public string? JobId { get; init; }
        public DateTime LaunchedUtc { get; init; } = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<int, Tracked> _byPid = new();

    /// <summary>Raised (off the UI thread) when a tracked client exits/crashes.</summary>
    public static event Action<Tracked>? Exited;

    public static IReadOnlyCollection<Tracked> All
    {
        get { Prune(); return _byPid.Values.ToList(); }
    }

    public static IEnumerable<Tracked> ForUser(long userId) => All.Where(t => t.UserId == userId);

    /// <summary>
    /// Called shortly after a launch. Grabs the newest RobloxPlayerBeta process that
    /// isn't already tracked and associates it with this account/place.
    /// </summary>
    public static void RegisterNewest(Account acc, long placeId, string? jobId)
    {
        var procs = Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            var candidate = procs
                .Where(p => { try { return !_byPid.ContainsKey(p.Id) && !p.HasExited; } catch { return false; } })
                .OrderByDescending(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } })
                .FirstOrDefault();

            if (candidate == null) return;

            _byPid[candidate.Id] = new Tracked
            {
                UserId = acc.UserId,
                Alias = acc.DisplayNameOrUser,
                Cookie = acc.Cookie,
                Pid = candidate.Id,
                PlaceId = placeId,
                JobId = jobId
            };
        }
        catch { }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>Drops entries whose process has exited, firing <see cref="Exited"/> for each.</summary>
    public static void Prune()
    {
        foreach (var kv in _byPid.ToArray())
        {
            bool gone;
            try
            {
                using var p = Process.GetProcessById(kv.Key);
                gone = p.HasExited;
            }
            catch { gone = true; } // process no longer exists

            if (gone && _byPid.TryRemove(kv.Key, out var t))
            {
                try { Exited?.Invoke(t); } catch { }
            }
        }
    }

    /// <summary>Live main-window handle for a tracked PID (0 until the client has a window).</summary>
    public static IntPtr WindowHandle(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.MainWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Working-set bytes for a tracked PID, or -1 if it's gone.</summary>
    public static long MemoryBytes(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.WorkingSet64; }
        catch { return -1; }
    }

    public static void Forget(int pid) => _byPid.TryRemove(pid, out _);
}
