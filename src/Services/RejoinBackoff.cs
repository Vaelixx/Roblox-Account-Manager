namespace RobloxAccountManager.Services;

/// <summary>
/// How long auto-rejoin waits before relaunching a client that crashed (or disconnected). It never
/// gives up while rejoin is on for the account; it slows down instead. The first rejoins in a row are
/// immediate; after that each wait is longer, up to a ceiling, so a game that crashes instantly is
/// retried a few times an hour instead of in a tight loop. A client that ran for
/// <see cref="StableAfter"/> resets the streak: that crash was a one-off, not a loop.
/// </summary>
public static class RejoinBackoff
{
    /// <summary>Rejoins in a row that happen straight away.</summary>
    public const int FreeRejoins = 3;

    /// <summary>A client that ran at least this long ends the streak.</summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Immediate = TimeSpan.FromSeconds(3);   // let the old process die
    private static readonly TimeSpan[] Steps =
        { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15) };

    /// <summary>Wait before the next rejoin, given how many rejoins already happened in a row.</summary>
    public static TimeSpan DelayFor(int streak)
        => streak < FreeRejoins ? Immediate : Steps[Math.Min(streak - FreeRejoins, Steps.Length - 1)];

    /// <summary>The streak after a client that ran for <paramref name="uptime"/> ended.</summary>
    public static int StreakAfterExit(int streak, TimeSpan uptime) => uptime >= StableAfter ? 0 : streak;
}
