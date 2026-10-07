using System.Text.Json.Serialization;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Models;

/// <summary>
/// A named destination bookmark, shown in the launch bar's saved-places dropdown and selectable in
/// presets: a place, optionally with what was in the server box — a Job ID or a private-server /
/// share link — so a private server never has to be pasted again. Several can point at the same
/// game ("Adopt Me — public", "Adopt Me — my VIP").
/// </summary>
public class SavedPlace : RobloxAccountManager.Mvvm.ObservableObject
{
    /// <summary>Stable id presets refer to; assigned on load for bookmarks saved before it existed.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public long PlaceId { get; set; }

    /// <summary>The launch bar's server box when this was saved. A private link is a credential, so it is stored encrypted.</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string Server { get; set; } = "";

    // Cached so the game icon doesn't need re-resolving on every popup open.
    public long UniverseId { get; set; }

    private string? _iconUrl;
    public string? IconUrl { get => _iconUrl; set => SetField(ref _iconUrl, value); }

    /// <summary>Second line in the dropdown: the place and what kind of server it goes to (never the link itself).</summary>
    [JsonIgnore]
    public string Detail
    {
        get
        {
            if (Server.Length == 0) return PlaceId.ToString();
            var parsed = JoinLinks.Parse(Server);
            return parsed.LinkCode != null || parsed.ShareCode != null
                ? L.T("Places.Detail.Private", PlaceId)
                : L.T("Places.Detail.Server", PlaceId);
        }
    }
}
