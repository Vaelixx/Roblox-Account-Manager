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
    public Dictionary<string, string> Fields { get; set; } = new();
    public DateTime LastUse { get; set; } = DateTime.Now;

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
