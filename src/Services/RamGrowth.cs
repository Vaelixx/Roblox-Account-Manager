namespace RobloxAccountManager.Services;

/// <summary>
/// Spots a client whose memory keeps climbing (a game leaking memory). Its settled size is the
/// first sample taken once it has run <see cref="SettleTime"/>; it is flagged once it reaches
/// <c>factor</c> times that size on <see cref="Confirmations"/> samples in a row, so a single
/// spike (a big area loading) doesn't count.
/// </summary>
public sealed class RamGrowth
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(10);
    public const int Confirmations = 3;

    public long BaselineMb { get; private set; }
    private int _over;

    /// <summary>Feeds one sample; true when the client should be restarted.</summary>
    public bool Observe(TimeSpan uptime, long mb, double factor)
    {
        if (mb <= 0) return false;
        if (BaselineMb == 0)
        {
            if (uptime >= SettleTime) BaselineMb = mb;
            return false;
        }
        _over = mb >= BaselineMb * Math.Max(1.2, factor) ? _over + 1 : 0;
        return _over >= Confirmations;
    }
}
