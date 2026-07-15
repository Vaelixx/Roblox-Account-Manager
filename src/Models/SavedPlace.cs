namespace RobloxAccountManager.Models;

/// <summary>A named Place ID bookmark, shown in the launch bar's saved-places dropdown.</summary>
public class SavedPlace : RobloxAccountManager.Mvvm.ObservableObject
{
    public string Name { get; set; } = "";
    public long PlaceId { get; set; }

    // Cached so the game icon doesn't need re-resolving on every popup open.
    public long UniverseId { get; set; }

    private string? _iconUrl;
    public string? IconUrl { get => _iconUrl; set => SetField(ref _iconUrl, value); }
}
