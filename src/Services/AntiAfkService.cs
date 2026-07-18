using System.Diagnostics;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Services;

/// <summary>
/// Keeps launched Roblox clients from being idle-kicked. Every N minutes it walks
/// each tracked window: remembers the window the user is currently on, focuses the
/// Roblox client, sends a single harmless key tap, then restores the previous window.
/// One input per interval — nothing that plays the game for you.
/// </summary>
public static class AntiAfkService
{
    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    // 0 = idle, 1 = a pass is running. Interlocked so the periodic tick and a manual
    // RunOnce ("Test now") can't both pass the guard and send overlapping key taps.
    private static int _running;

    /// <summary>Re-reads settings and starts/stops the loop accordingly. Call after any settings change.</summary>
    public static void Apply()
    {
        var s = SettingsService.Current;
        if (s.AntiAfkEnabled) Start(Math.Max(1, s.AntiAfkIntervalMinutes));
        else Stop();
    }

    private static void Start(int intervalMinutes)
    {
        lock (_gate)
        {
            var period = TimeSpan.FromMinutes(intervalMinutes);
            if (_timer == null)
                _timer = new System.Threading.Timer(_ => Tick(), null, period, period);
            else
                _timer.Change(period, period);
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
        var s = SettingsService.Current;
        if (!s.AntiAfkEnabled) { Stop(); return; }   // disabled since the timer was armed
        Pass();
    }

    /// <summary>
    /// One full anti-AFK pass. Remembers the user's active window, then for every tracked
    /// Roblox client: focus it, tap the configured key once, move on. Finally restores focus.
    /// Guarded so passes never overlap (the timer tick and a manual "Test now" can't collide).
    /// </summary>
    private static void Pass()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;   // never overlap passes
        try
        {
            var s = SettingsService.Current;
            ushort vk = VkForKey(s.AntiAfkKey);
            IntPtr userWindow = Win32.GetForegroundWindow();   // where the user was

            foreach (var t in ProcessRegistry.All)
            {
                IntPtr hWnd = ProcessRegistry.WindowHandle(t.Pid);
                if (hWnd == IntPtr.Zero) continue;

                Win32.ForceForeground(hWnd);
                Thread.Sleep(250);                 // let the window actually take focus
                Win32.TapKey(vk);
                Thread.Sleep(150);
            }

            // Return the user to whatever they were doing.
            if (s.AntiAfkRestoreFocus && userWindow != IntPtr.Zero)
                Win32.ForceForeground(userWindow);
        }
        catch { }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>Runs one anti-AFK pass immediately regardless of the enabled flag (for a "Test now" button).</summary>
    public static void RunOnce() => System.Threading.Tasks.Task.Run(Pass);

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
