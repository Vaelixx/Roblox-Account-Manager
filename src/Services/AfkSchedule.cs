namespace RobloxAccountManager.Services;

/// <summary>
/// Interval arithmetic for Anti-AFK, kept free of Win32 so it can be tested. Every client gets its
/// own timeline: a random first delay spreads clients that were found together (manager start,
/// Anti-AFK switched on, a preset of five) instead of focusing all their windows in the same second,
/// and an optional random interval keeps them spread. The steps of each key press (waiting for
/// focus, holding the key, the pause before the next client) vary every time too, so the input never
/// repeats exactly. This is timing jitter only — the action itself is still the one configured key tap.
/// </summary>
public static class AfkSchedule
{
    /// <summary>Time until the next key press after one was sent.</summary>
    public static TimeSpan NextInterval(int minMinutes, int maxMinutes, bool randomize, Random rng)
    {
        int min = Math.Max(1, minMinutes);
        int max = Math.Max(min, maxMinutes);
        if (!randomize || max == min) return TimeSpan.FromMinutes(min);
        return TimeSpan.FromSeconds(rng.Next(min * 60, max * 60 + 1));
    }

    /// <summary>
    /// Delay before a newly seen client's first key press: somewhere between half an interval and a
    /// full one, so it is never later than a fixed schedule would have been.
    /// </summary>
    public static TimeSpan FirstDelay(int minMinutes, int maxMinutes, bool randomize, Random rng)
    {
        var full = NextInterval(minMinutes, maxMinutes, randomize, rng);
        return TimeSpan.FromSeconds(full.TotalSeconds * (0.5 + 0.5 * rng.NextDouble()));
    }

    /// <summary>
    /// Retry delay after a key press could not be delivered (the window would not take focus): a minute,
    /// for as long as it keeps failing. Falling back to the normal interval after a few failures let the
    /// gap between two delivered presses pass Roblox's 20-minute idle kick.
    /// </summary>
    public static TimeSpan AfterFailure(TimeSpan normal)
        => normal < TimeSpan.FromMinutes(1) ? normal : TimeSpan.FromMinutes(1);

    /// <summary>Pause between two key presses of one visit, in milliseconds.</summary>
    public static int PressGapMs(Random rng) => rng.Next(800, 2501);

    /// <summary>Waits for one key press, in milliseconds: after focusing, key held down, after the tap, before the next client.</summary>
    public readonly record struct TapTiming(int SettleMs, int HoldMs, int AfterMs, int GapMs);

    /// <summary>
    /// Fresh random waits for one visit. The window keeps focus for <paramref name="focusSeconds"/> plus a
    /// random bit before the first key; one coming back from minimized gets an extra second to redraw.
    /// </summary>
    public static TapTiming RandomTiming(bool wasMinimized, Random rng, int focusSeconds = 0) => new(
        SettleMs: Math.Clamp(focusSeconds, 0, 30) * 1000 + rng.Next(350, 901) + (wasMinimized ? 1000 : 0),
        HoldMs: rng.Next(60, 201),
        AfterMs: rng.Next(150, 601),
        GapMs: rng.Next(400, 1501));
}
