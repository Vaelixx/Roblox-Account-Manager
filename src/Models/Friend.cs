using System.Text.Json.Serialization;
using RobloxAccountManager.Mvvm;

namespace RobloxAccountManager.Models;

/// <summary>
/// One Roblox friend of a manager account, shown in the Friends tab. The base list
/// (id / username / display name) loads first; presence + headshot are filled in live
/// afterwards, which is why the mutable fields raise change notifications.
/// </summary>
public class Friend : ObservableObject
{
    public long UserId { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";

    [JsonIgnore]
    public string DisplayNameOrUser =>
        string.IsNullOrEmpty(DisplayName)
            ? (string.IsNullOrEmpty(Username) ? "Unknown" : Username)
            : DisplayName;

    [JsonIgnore] public string AtUsername => "@" + Username;

    /// <summary>Readable fallback if a control ever shows the raw object.</summary>
    public override string ToString() => DisplayNameOrUser;

    private string _presence = "Offline";
    /// <summary>"Online" | "In Game" | "In Studio" | "Offline".</summary>
    public string Presence
    {
        get => _presence;
        set
        {
            if (SetField(ref _presence, value))
            {
                OnPropertyChanged(nameof(PresenceColor));
                OnPropertyChanged(nameof(IsInGame));
                OnPropertyChanged(nameof(CanJoin));
                OnPropertyChanged(nameof(StatusLine));
                OnPropertyChanged(nameof(IsSubPlace));
                OnPropertyChanged(nameof(SubPlaceTip));
            }
        }
    }

    /// <summary>Dot colour — mirrors the palette used for account presence.</summary>
    public string PresenceColor => Presence switch
    {
        "Online"    => "#3FB950",
        "In Game"   => "#7B61FF",
        "In Studio" => "#E3B341",
        _           => "#63636C"
    };

    private string _lastLocation = "";
    public string LastLocation
    {
        get => _lastLocation;
        set { if (SetField(ref _lastLocation, value)) OnPropertyChanged(nameof(StatusLine)); }
    }

    /// <summary>One-line status under the name (game name when in-game, else the presence).</summary>
    [JsonIgnore]
    public string StatusLine =>
        Presence == "In Game" && !string.IsNullOrWhiteSpace(LastLocation)
            ? $"In Game · {LastLocation}"
            : Presence;

    private string _headshotUrl = "";
    public string HeadshotUrl { get => _headshotUrl; set => SetField(ref _headshotUrl, value); }

    // ---- join target (populated from rich presence) ----
    private long _placeId;
    public long PlaceId
    {
        get => _placeId;
        set { if (SetField(ref _placeId, value)) { OnPropertyChanged(nameof(CanJoin)); OnPropertyChanged(nameof(IsSubPlace)); OnPropertyChanged(nameof(SubPlaceTip)); } }
    }

    private long _rootPlaceId;
    public long RootPlaceId
    {
        get => _rootPlaceId;
        set { if (SetField(ref _rootPlaceId, value)) { OnPropertyChanged(nameof(CanJoin)); OnPropertyChanged(nameof(IsSubPlace)); OnPropertyChanged(nameof(SubPlaceTip)); } }
    }

    private string? _jobId;
    public string? JobId { get => _jobId; set => SetField(ref _jobId, value); }

    [JsonIgnore] public bool IsInGame => Presence == "In Game";

    /// <summary>True only when the friend is in a game we can follow them into.</summary>
    [JsonIgnore] public bool CanJoin => IsInGame && (RootPlaceId > 0 || PlaceId > 0);

    /// <summary>
    /// True when the friend is in a *sub-place* of a universe rather than its root game —
    /// i.e. a teleport/co-edit place whose PlaceId differs from the universe's RootPlaceId.
    /// Following them still lands in the right universe, but they're not on the main place.
    /// </summary>
    [JsonIgnore]
    public bool IsSubPlace =>
        IsInGame && PlaceId > 0 && RootPlaceId > 0 && PlaceId != RootPlaceId;

    /// <summary>Tooltip explaining the sub-place badge.</summary>
    [JsonIgnore]
    public string SubPlaceTip => IsSubPlace
        ? $"Sub-place of this game (place {PlaceId} · root {RootPlaceId})"
        : "";
}
