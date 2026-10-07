namespace RobloxAccountManager.Services;

/// <summary>
/// Interval arithmetic for Anti-AFK, kept free of Win32 so it can be tested. Every client gets its
/// own timeline: a random first delay spreads clients that were found together (manager start,
/// Anti-AFK switched on, a preset of five) instead of focusing all their windows in the same second,
/// and an optional random interval keeps them spread. This is timing jitter only — the action
/// itself is still the one configured key tap.
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
    /// Retry delay after a key press could not be delivered (the window would not take focus). Quick
    /// retries a few times, then back to the normal interval so a stubborn window isn't poked forever.
    /// </summary>
    public static TimeSpan AfterFailure(int consecutiveFailures, TimeSpan normal)
        => consecutiveFailures <= 3 ? TimeSpan.FromMinutes(1) : normal;
}
