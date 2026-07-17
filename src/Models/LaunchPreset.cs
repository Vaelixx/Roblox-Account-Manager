namespace RobloxAccountManager.Models;

/// <summary>
/// A saved "one-click" launch setup: a set of account aliases plus a place/server and
/// a join delay. Lets the user store "Farm-Setup", "Trade-Alts" etc. and start them all.
/// </summary>
public class LaunchPreset
{
    public string Name { get; set; } = "New preset";
    public List<string> Aliases { get; set; } = new();  // account aliases (fallback: usernames)
    public long PlaceId { get; set; }
    public string JobId { get; set; } = "";
    public int JoinDelaySeconds { get; set; } = 8;
}
