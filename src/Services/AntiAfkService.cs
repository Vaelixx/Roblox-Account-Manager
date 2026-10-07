using System.Collections.Concurrent;

namespace RobloxAccountManager.Services;

/// <summary>
/// Keeps launched Roblox clients from being idle-kicked. Each tracked client has its own schedule
/// (<see cref="AfkSchedule"/>); when one is due, the service remembers the window the user is on,
/// focuses the client, sends a single harmless key tap, then restores the previous window.
/// One input per interval — nothing that plays the game for you.
/// </summary>
public static class AntiAfkService
{
    // How often due clients are looked for. Only decides how late a key press can be; the
    // schedule itself is per client.
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(15);

    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    // 0 = idle, 1 = a pass is running. Interlocked so the periodic tick and a manual
    // RunOnce ("Test now") can't both pass the guard and send overlapping key taps.
    private static int _running;

    private sealed class ClientState
    {
        public DateTime NextDueUtc;
        public DateTime? LastSentUtc;
        public int Failures;
    }

    private static readonly ConcurrentDictionary<int, ClientState> _clients = new();

    /// <summary>When a client last got its key press and roughly when the next one is due (UTC; null = unknown).</summary>
    public static (DateTime? Last, DateTime? Next) StatusFor(int pid)
    {
        if (!_clients.TryGetValue(pid, out var st)) return (null, null);
        return (st.LastSentUtc, _timer != null ? st.NextDueUtc : null);
    }

    /// <summary>Re-reads settings and starts/stops the loop accordingly. Call after any settings change.</summary>
    public static void Apply()
    {
        var s = SettingsService.Current;
        // New interval settings: give every client a fresh (staggered) schedule under them.
        foreach (var st in _clients.Values) st.NextDueUtc = DateTime.MinValue;
        if (s.AntiAfkEnabled) Start();
        else Stop();
    }

    private static void Start()
    {
        lock (_gate)
        {
            _timer ??= new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, TickPeriod);
        }
    }

    public static void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static void Tick()
    {
        try
        {
            var s = SettingsService.Current;
            if (!s.AntiAfkEnabled) { Stop(); return; }   // disabled since the timer was armed

            var now = DateTime.UtcNow;
            var live = ProcessRegistry.All.ToList();

            foreach (int pid in _clients.Keys.Except(live.Select(t => t.Pid)).ToList())
                _clients.TryRemove(pid, out _);

            var due = new List<ProcessRegistry.Tracked>();
            foreach (var t in live)
            {
                var st = _clients.GetOrAdd(t.Pid, _ => new ClientState { NextDueUtc = DateTime.MinValue });
                if (st.NextDueUtc == DateTime.MinValue)
                    st.NextDueUtc = now + AfkSchedule.FirstDelay(s.AntiAfkIntervalMinutes, s.AntiAfkIntervalMaxMinutes, s.AntiAfkRandomize, Random.Shared);
                else if (st.NextDueUtc <= now)
                    due.Add(t);
            }

            if (due.Count > 0) Pass(due);
        }
        catch (Exception ex) { DiagnosticsService.Warn("anti-afk", "Anti-AFK tick failed", ex); }   // a throwing timer callback kills the process
    }

    /// <summary>
    /// Sends one key press to each of <paramref name="clients"/>, then puts the user back where they
    /// were. Guarded so passes never overlap (the timer tick and a manual "Test now" can't collide).
    /// Returns how many clients got the key press.
    /// </summary>
    private static int Pass(IReadOnlyList<ProcessRegistry.Tracked> clients)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return 0;   // never overlap passes
        int sent = 0;
        try
        {
            var s = SettingsService.Current;
            ushort vk = VkForKey(s.AntiAfkKey);
            IntPtr userWindow = Win32.GetForegroundWindow();   // where the user was

            foreach (var t in clients)
            {
                var st = _clients.GetOrAdd(t.Pid, _ => new ClientState());
                var interval = AfkSchedule.NextInterval(s.AntiAfkIntervalMinutes, s.AntiAfkIntervalMaxMinutes, s.AntiAfkRandomize, Random.Shared);

                IntPtr hWnd = ProcessRegistry.WindowHandle(ProcessRegistry.TokenFor(t));
                if (hWnd == IntPtr.Zero) { st.NextDueUtc = DateTime.UtcNow + AfkSchedule.AfterFailure(++st.Failures, interval); continue; }

                // A client minimized on purpose (Ultra-Low AFK profile, "minimize all") goes back down afterwards.
                bool wasMinimized = Win32.IsIconic(hWnd);

                Win32.ForceForeground(hWnd);
                Thread.Sleep(250);                 // let the window actually take focus

                // Windows refuses SetForegroundWindow in plenty of situations (a fullscreen game
                // elsewhere, a UAC prompt, foreground lock). Sending the key anyway typed
                // Space / W into whatever the user was actually doing. Skip instead — a missed
                // anti-AFK tap is recoverable, a keystroke in someone's chat window is not.
                if (Win32.GetForegroundWindow() != hWnd)
                {
                    DiagnosticsService.Warn("anti-afk", $"Skipped {t.Alias}: its window would not take focus");
                    st.NextDueUtc = DateTime.UtcNow + AfkSchedule.AfterFailure(++st.Failures, interval);
                    if (wasMinimized) Win32.ShowWindow(hWnd, SW_SHOWMINNOACTIVE);
                    continue;
                }

                Win32.TapKey(vk);
                Thread.Sleep(150);
                if (wasMinimized) Win32.ShowWindow(hWnd, SW_SHOWMINNOACTIVE);

                st.LastSentUtc = DateTime.UtcNow;
                st.Failures = 0;
                st.NextDueUtc = DateTime.UtcNow + interval;
                sent++;
            }

            // Return the user to whatever they were doing.
            if (s.AntiAfkRestoreFocus && userWindow != IntPtr.Zero)
                Win32.ForceForeground(userWindow);
        }
        catch (Exception ex) { DiagnosticsService.Warn("anti-afk", "Anti-AFK pass failed", ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
        return sent;
    }

    private const int SW_SHOWMINNOACTIVE = 7;

    /// <summary>Runs one anti-AFK pass over every client immediately, regardless of the enabled flag (the "Test now" button).</summary>
    public static void RunOnce() => System.Threading.Tasks.Task.Run(() => Pass(ProcessRegistry.All.ToList()));

    // Common, mostly game-safe keys. Space (jump) is the most reliable at resetting
    // Roblox's idle timer; the rest are offered for games where jumping matters.
    private static ushort VkForKey(string? name) => (name ?? "Space").Trim().ToLowerInvariant() switch
    {
        "space" => 0x20,
        "shift" => 0x10,
        "ctrl" or "control" => 0x11,
        "w" => 0x57,
        "a" => 0x41,
        "s" => 0x53,
        "d" => 0x44,
        "0" => 0x30,
        "f13" => 0x7C,          // no-op in virtually every game, but still counts as input
        _ => 0x20
    };
}
