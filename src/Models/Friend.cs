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
    public long PlaceId { get => _placeId; set { if (SetField(ref _placeId, value)) OnPropertyChanged(nameof(CanJoin)); } }

    private long _rootPlaceId;
    public long RootPlaceId { get => _rootPlaceId; set { if (SetField(ref _rootPlaceId, value)) OnPropertyChanged(nameof(CanJoin)); } }

    private string? _jobId;
    public string? JobId { get => _jobId; set => SetField(ref _jobId, value); }

    [JsonIgnore] public bool IsInGame => Presence == "In Game";

    /// <summary>True only when the friend is in a game we can follow them into.</summary>
    [JsonIgnore] public bool CanJoin => IsInGame && (RootPlaceId > 0 || PlaceId > 0);
}
