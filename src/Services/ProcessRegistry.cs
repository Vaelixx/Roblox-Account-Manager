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
    /// <summary>The only process name a Roblox game client ever has.</summary>
    private const string ClientProcess = "RobloxPlayerBeta";

    /// <summary>
    /// How long a client must have been running before it counts as "started outside the app".
    /// A launch of ours registers a few seconds after Process.Start, so adopting any younger
    /// process would let the adopter steal a client the launch is still trying to claim.
    /// </summary>
    private static readonly TimeSpan AdoptionGrace = TimeSpan.FromSeconds(30);

    /// <summary>Stable identity for one OS process instance. PID alone is not stable on Windows.</summary>
    public readonly record struct ProcessToken(int Pid, DateTime StartTimeLocal);

    public class Tracked
    {
        public long UserId { get; init; }
        public string Alias { get; init; } = "";
        public string Cookie { get; init; } = "";
        public int Pid { get; set; }
        public long PlaceId { get; init; }
        public string? JobId { get; init; }
        public DateTime LaunchedUtc { get; init; } = DateTime.UtcNow;

        /// <summary>True for clients adopted from outside the manager (see <see cref="AdoptUntracked"/>).</summary>
        public bool IsExternal { get; init; }

        /// <summary>
        /// Process identity, captured at registration. Windows recycles PIDs aggressively, and
        /// every consumer of this registry acts on the PID — the RAM monitor kills it, Anti-AFK
        /// types into its window, the scheduler closes it. Without an identity check those
        /// actions eventually land on whatever unrelated program inherited the number.
        /// </summary>
        public string ProcessName { get; init; } = ClientProcess;
        public DateTime StartTimeLocal { get; init; }

        /// <summary>How long this client has been running.</summary>
        public TimeSpan Uptime => DateTime.UtcNow - LaunchedUtc;

        /// <summary>
        /// Set when the manager (or the user through it) is closing this client on purpose. The exit
        /// is still reported — playtime is booked — but the crash watchdog leaves it alone instead of
        /// answering a deliberate close with an auto-rejoin.
        /// </summary>
        public volatile bool ClosingIntentionally;
    }

    private static readonly ConcurrentDictionary<int, Tracked> _byPid = new();

    /// <summary>Raised (off the UI thread) when a tracked client exits/crashes.</summary>
    public static event Action<Tracked>? Exited;

    /// <summary>Raised (off the UI thread) whenever a client was registered or dropped.</summary>
    public static event Action? Changed;

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Flags a tracked client as being closed on purpose (see <see cref="Tracked.ClosingIntentionally"/>).</summary>
    public static void MarkClosing(int pid)
    {
        if (_byPid.TryGetValue(pid, out var t)) t.ClosingIntentionally = true;
    }

    public static void MarkClosing(ProcessToken token)
    {
        if (_byPid.TryGetValue(token.Pid, out var t)
            && SameStart(t.StartTimeLocal, token.StartTimeLocal))
            t.ClosingIntentionally = true;
    }

    /// <summary>Pids tracked for an account right now.</summary>
    public static int CountFor(long userId) => _byPid.Values.Count(t => t.UserId == userId && !t.IsExternal);

    public static IReadOnlyCollection<Tracked> All
    {
        get { Prune(); return _byPid.Values.ToList(); }
    }

    public static IEnumerable<Tracked> ForUser(long userId) => All.Where(t => t.UserId == userId);

    /// <summary>
    /// Called shortly after a launch. Claims the oldest not-yet-attributed client that started
    /// after <paramref name="launchedAt"/> and associates it with this account/place. Returns the
    /// pid it claimed, or 0 when no new client could be attributed (caller should retry).
    /// </summary>
    /// <param name="launchedAt">
    /// Local time the launch was fired. Clients that already existed before it belong to an
    /// earlier launch — without this bound a launch that failed silently would steal someone
    /// else's client.
    /// </param>
    public static int RegisterNewest(Account acc, long placeId, string? jobId, DateTime? launchedAt = null)
    {
        var procs = Process.GetProcessesByName(ClientProcess);
        try
        {
            // Oldest-first, NOT newest-first. Two launches a second apart both register a few
            // seconds later; picking "newest" hands the first launch the second launch's window
            // and vice versa — a deterministic swap that puts the wrong cookie on a rejoin and
            // makes "close previous client" kill the other account.
            var candidates = procs
                .Select(p =>
                {
                    try { return (proc: p, start: p.StartTime, exited: p.HasExited); }
                    catch { return (proc: p, start: DateTime.MaxValue, exited: true); }
                })
                .Where(x => !x.exited && (launchedAt == null || x.start >= launchedAt.Value))
                .OrderBy(x => x.start);

            foreach (var candidate in candidates)
            {
                var tracked = new Tracked
                {
                    UserId = acc.UserId,
                    Alias = acc.DisplayNameOrUser,
                    Cookie = acc.Cookie,
                    Pid = candidate.proc.Id,
                    PlaceId = placeId,
                    JobId = jobId,
                    ProcessName = ClientProcess,
                    StartTimeLocal = candidate.start,
                };

                if (_byPid.TryAdd(candidate.proc.Id, tracked))
                {
                    RaiseChanged();
                    return candidate.proc.Id;
                }

                if (_byPid.TryGetValue(candidate.proc.Id, out var existing)
                    && existing.IsExternal
                    && _byPid.TryUpdate(candidate.proc.Id, tracked, existing))
                {
                    RaiseChanged();
                    return candidate.proc.Id;
                }
            }

            return 0;
        }
        catch { return 0; }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
    }

    /// <summary>
    /// Clients started outside the manager (website Play button, Roblox home screen, a desktop
    /// shortcut) are invisible to Anti-AFK, the RAM monitor and the window grid because nothing
    /// ever registered them. Adopt them under a placeholder identity so those tools still work.
    /// Returns how many were newly adopted.
    /// </summary>
    /// <remarks>
    /// Only clients older than <see cref="AdoptionGrace"/> are eligible. A launch of ours needs a
    /// few seconds to find its window, and an adopted pid would otherwise be claimed here first
    /// and never attributed to the account that started it.
    /// </remarks>
    public static int AdoptUntracked()
    {
        int adopted = 0;
        var procs = Process.GetProcessesByName(ClientProcess);
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited || _byPid.ContainsKey(p.Id)) continue;

                    DateTime start;
                    try { start = p.StartTime; } catch { continue; }   // can't age it -> leave it alone
                    if (DateTime.Now - start < AdoptionGrace) continue;

                    // TryAdd, not the indexer: a launch in flight may claim this pid moments
                    // later and its real account/place must win over the placeholder.
                    if (_byPid.TryAdd(p.Id, new Tracked
                    {
                        UserId = 0,
                        Alias = "External client",
                        Cookie = "",
                        Pid = p.Id,
                        PlaceId = 0,
                        JobId = null,
                        LaunchedUtc = start.ToUniversalTime(),
                        IsExternal = true,
                        ProcessName = ClientProcess,
                        StartTimeLocal = start,
                    })) adopted++;
                }
                catch { /* raced with exit */ }
            }
        }
        catch { }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
        if (adopted > 0) RaiseChanged();
        return adopted;
    }

    /// <summary>Drops entries whose process has exited, firing <see cref="Exited"/> for each.</summary>
    public static void Prune()
    {
        bool removed = false;
        foreach (var kv in _byPid.ToArray())
        {
            bool gone;
            try
            {
                using var p = Process.GetProcessById(kv.Key);
                gone = p.HasExited || !IsSameProcess(p, kv.Value);
            }
            catch { gone = true; } // process no longer exists

            if (gone && ((ICollection<KeyValuePair<int, Tracked>>)_byPid).Remove(kv))
            {
                removed = true;
                try { Exited?.Invoke(kv.Value); } catch { }
            }
        }
        if (removed) RaiseChanged();
    }

    /// <summary>
    /// True when the live process really is the one that was registered. Guards against PID
    /// reuse: name and start time together are unique enough that no other program can be
    /// mistaken for a tracked client.
    /// </summary>
    private static bool IsSameProcess(Process live, Tracked tracked)
    {
        try
        {
            if (!string.Equals(live.ProcessName, tracked.ProcessName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (tracked.StartTimeLocal == default) return true;   // registered before this check existed
            return Math.Abs((live.StartTime - tracked.StartTimeLocal).TotalSeconds) < 2;
        }
        catch { return false; }
    }

    private static bool SameStart(DateTime a, DateTime b)
        => a == default || b == default || Math.Abs((a - b).TotalSeconds) < 2;

    public static ProcessToken TokenFor(Tracked tracked) => new(tracked.Pid, tracked.StartTimeLocal);

    public static bool TryGetToken(int pid, out ProcessToken token)
    {
        if (_byPid.TryGetValue(pid, out var tracked))
        {
            token = TokenFor(tracked);
            return true;
        }
        token = default;
        return false;
    }

    /// <summary>True only when the live process is the exact process currently tracked under its PID.</summary>
    internal static bool IsTrackedProcess(Process live)
    {
        if (!_byPid.TryGetValue(live.Id, out var tracked)) return false;
        return IsSameProcess(live, tracked);
    }

    internal static bool IsTrackedProcess(Process live, ProcessToken expected)
    {
        if (live.Id != expected.Pid) return false;
        if (!_byPid.TryGetValue(live.Id, out var tracked)) return false;
        return SameStart(tracked.StartTimeLocal, expected.StartTimeLocal)
               && IsSameProcess(live, tracked)
               && SameStart(live.StartTime, expected.StartTimeLocal);
    }

    /// <summary>Live main-window handle for a tracked PID (0 until the client has a window).</summary>
    public static IntPtr WindowHandle(ProcessToken token)
    {
        try
        {
            using var p = Process.GetProcessById(token.Pid);
            return IsTrackedProcess(p, token) ? p.MainWindowHandle : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    public static IntPtr WindowHandle(int pid)
        => TryGetToken(pid, out var token) ? WindowHandle(token) : IntPtr.Zero;

    /// <summary>Working-set bytes for a tracked PID, or -1 if it's gone.</summary>
    public static long MemoryBytes(ProcessToken token)
    {
        try
        {
            using var p = Process.GetProcessById(token.Pid);
            return IsTrackedProcess(p, token) ? p.WorkingSet64 : -1;
        }
        catch { return -1; }
    }

    public static long MemoryBytes(int pid)
        => TryGetToken(pid, out var token) ? MemoryBytes(token) : -1;

    /// <summary>Drops a pid without raising <see cref="Exited"/> (the pid turned out not to be a client).</summary>
    public static void Forget(int pid)
    {
        if (_byPid.TryRemove(pid, out _)) RaiseChanged();
    }
}
