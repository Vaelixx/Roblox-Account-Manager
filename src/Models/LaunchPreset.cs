using System.Text.Json.Serialization;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Models;

/// <summary>
/// A saved "one-click" launch setup: a set of account aliases plus a destination and
/// a join delay. Lets the user store "Farm-Setup", "Trade-Alts" etc. and start them all.
/// </summary>
public class LaunchPreset
{
    public string Name { get; set; } = "New preset";
    public List<string> Aliases { get; set; } = new();  // account aliases (fallback: usernames)
    public long PlaceId { get; set; }

    /// <summary>
    /// Where the accounts go. Missing in presets saved before destinations existed; those only had
    /// <see cref="PlaceId"/> + <see cref="JobId"/> and are classified by <see cref="NormalizeDestination"/>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JoinKind Destination { get; set; } = JoinKind.Place;

    /// <summary>Specific public server, for <see cref="JoinKind.Server"/>.</summary>
    public string JobId { get; set; } = "";

    /// <summary>
    /// The private-server link as the user pasted it (classic <c>privateServerLinkCode</c> link or a
    /// modern share link). It is the credential for that server, so it is stored encrypted.
    /// </summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string PrivateServerLink { get; set; } = "";

    /// <summary>Player to join, for <see cref="JoinKind.FollowUser"/>. The id is what launches; the name is for display.</summary>
    public long FollowUserId { get; set; }
    public string FollowUsername { get; set; } = "";

    /// <summary>
    /// A saved place (launch bar bookmark) this preset launches into instead of its own destination
    /// fields; empty = use the fields above. Editing the saved place updates every preset using it.
    /// </summary>
    public string SavedPlaceId { get; set; } = "";

    public int JoinDelaySeconds { get; set; } = 8;

    /// <summary>Up to this many extra seconds, picked at random, on top of <see cref="JoinDelaySeconds"/>.</summary>
    public int RandomDelaySeconds { get; set; } = 0;

    /// <summary>Performance profile id (<see cref="PerformanceProfiles"/>); empty = the normal graphics settings.</summary>
    public string PerformanceProfile { get; set; } = "";

    /// <summary>
    /// Upgrades a preset saved before destinations existed. Those stored whatever was typed into the
    /// "Server ID" box in <see cref="JobId"/> — including private-server links, which were then launched
    /// as a garbage Job ID. Idempotent; returns true when something changed.
    /// </summary>
    public bool NormalizeDestination()
    {
        if (Destination != JoinKind.Place || string.IsNullOrWhiteSpace(JobId)) return false;

        string legacy = JobId.Trim();
        JobId = "";
        if (JoinLinks.LooksLikeLink(legacy))
        {
            var parsed = JoinLinks.Parse(legacy);
            if (PlaceId <= 0 && parsed.PlaceId > 0) PlaceId = parsed.PlaceId;

            if (parsed.LinkCode != null || parsed.ShareCode != null)
            {
                Destination = JoinKind.PrivateServer;
                PrivateServerLink = legacy;
            }
            else if (parsed.JobId != null)
            {
                Destination = JoinKind.Server;
                JobId = parsed.JobId;
            }
            // else: a plain game link — the place is all it carried.
        }
        else
        {
            Destination = JoinKind.Server;
            JobId = legacy;
        }
        return true;
    }
}
