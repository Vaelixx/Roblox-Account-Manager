namespace RobloxAccountManager.Models;

/// <summary>A named Place ID bookmark, shown in the launch bar's saved-places dropdown.</summary>
public class SavedPlace
{
    public string Name { get; set; } = "";
    public long PlaceId { get; set; }
}
