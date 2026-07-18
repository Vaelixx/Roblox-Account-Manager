using System.Text.Json.Serialization;
using RobloxAccountManager.Mvvm;

namespace RobloxAccountManager.Models;

/// <summary>
/// A single Roblox account. Only the fields under "persisted" are written to disk
/// (encrypted); everything else is fetched live from Roblox and kept in memory.
/// </summary>
public class Account : ObservableObject
{
    // ---- persisted ----
    public string Cookie { get; set; } = "";           // .ROBLOSECURITY
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Group { get; set; } = "Default";
    public string BrowserTrackerId { get; set; } = "";
    private string _color = "";
    public string Color { get => _color; set { SetField(ref _color, value); OnPropertyChanged(nameof(HasColor)); } }  // optional tag colour (hex, e.g. #7B61FF)
    [JsonIgnore] public bool HasColor => !string.IsNullOrEmpty(_color);
    public Dictionary<string, string> Fields { get; set; } = new();
    public DateTime LastUse { get; set; } = DateTime.Now;

    public string TotpSecret { get; set; } = "";       // Base32 2FA secret (stored DPAPI-protected)
    public string ProxyUrl { get; set; } = "";          // optional per-account proxy (http://host:port)
    public string FFlags { get; set; } = "";            // per-account ClientAppSettings JSON (raw)

    private bool _autoRejoin;
    public bool AutoRejoin { get => _autoRejoin; set => SetField(ref _autoRejoin, value); }

    // Pinned accounts float to the top of their group in the list.
    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set => SetField(ref _isFavorite, value); }

    private string _alias = "";
    public string Alias
    {
        get => _alias;
        set => SetField(ref _alias, value ?? "");
    }

    private string _description = "";
    public string Description
    {
        get => _description;
        set => SetField(ref _description, value ?? "");
    }

    // ---- runtime (not serialized) ----
    private long _robux = -1;
    [JsonIgnore] public long Robux { get => _robux; set { SetField(ref _robux, value); OnPropertyChanged(nameof(RobuxDisplay)); } }
    [JsonIgnore] public string RobuxDisplay => _robux < 0 ? "—" : _robux.ToString("N0");

    private long _rap = -1;
    [JsonIgnore] public long Rap { get => _rap; set { SetField(ref _rap, value); OnPropertyChanged(nameof(RapDisplay)); } }
    [JsonIgnore] public string RapDisplay => _rap < 0 ? "—" : _rap.ToString("N0");

    private bool _isPremium;
    [JsonIgnore] public bool IsPremium { get => _isPremium; set => SetField(ref _isPremium, value); }

    private string _presence = "Offline";
    [JsonIgnore] public string Presence { get => _presence; set { SetField(ref _presence, value); OnPropertyChanged(nameof(PresenceColor)); } }

    [JsonIgnore]
    public string PresenceColor => Presence switch
    {
        "Online" => "#3FB950",
        "In Game" => "#7B61FF",
        "In Studio" => "#E3B341",
        _ => "#63636C"
    };

    private long _placeId;
    [JsonIgnore] public long PlaceId { get => _placeId; set { SetField(ref _placeId, value); OnPropertyChanged(nameof(IsSubPlace)); OnPropertyChanged(nameof(SubPlaceTip)); } }

    private long _rootPlaceId;
    [JsonIgnore] public long RootPlaceId { get => _rootPlaceId; set { SetField(ref _rootPlaceId, value); OnPropertyChanged(nameof(IsSubPlace)); OnPropertyChanged(nameof(SubPlaceTip)); } }

    // True when the account is in a sub-place (a different PlaceId than the experience's root PlaceId).
    [JsonIgnore] public bool IsSubPlace => PlaceId > 0 && RootPlaceId > 0 && PlaceId != RootPlaceId;

    [JsonIgnore] public string SubPlaceTip => IsSubPlace ? $"Sub-place (place {PlaceId} in experience {RootPlaceId})" : "";

    private string? _thumbnailUrl;
    [JsonIgnore] public string? ThumbnailUrl { get => _thumbnailUrl; set => SetField(ref _thumbnailUrl, value); }

    private bool _isValid = true;
    [JsonIgnore] public bool IsValid { get => _isValid; set => SetField(ref _isValid, value); }

    private bool _isBusy;
    [JsonIgnore] public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    // Ticked in the account list for multi-select / multi-launch.
    private bool _isChecked;
    [JsonIgnore] public bool IsChecked { get => _isChecked; set => SetField(ref _isChecked, value); }

    [JsonIgnore] public string DisplayNameOrUser => string.IsNullOrEmpty(Alias) ? (string.IsNullOrEmpty(Username) ? "Unknown" : Username) : Alias;
    [JsonIgnore] public string Initials
    {
        get
        {
            var s = string.IsNullOrEmpty(Username) ? Alias : Username;
            return string.IsNullOrEmpty(s) ? "?" : s.Substring(0, 1).ToUpperInvariant();
        }
    }

    /// <summary>
    /// Human-readable fallback so any control that shows the raw object (e.g. a ComboBox
    /// selection box before its item template has been realized) renders the account name
    /// instead of "RobloxAccountManager.Models.Account".
    /// </summary>
    public override string ToString() => DisplayNameOrUser;

    // Helpers to raise change notifications after a live refresh.
    public void RaiseIdentityChanged()
    {
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayNameOrUser));
        OnPropertyChanged(nameof(Initials));
    }

    public string GetField(string key) => Fields.TryGetValue(key, out var v) ? v : "";
    public void SetFieldValue(string key, string value) => Fields[key] = value;
}
