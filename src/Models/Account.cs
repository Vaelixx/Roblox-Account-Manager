using System.Text.Json.Serialization;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Models;

/// <summary>
/// A single Roblox account. Persisted fields are written (encrypted) by <see cref="AccountStore"/>;
/// everything marked runtime is fetched live from Roblox and only kept in memory.
/// </summary>
public class Account : ObservableObject
{
    // ================================================================ persisted

    private string _cookie = "";
    /// <summary>The .ROBLOSECURITY session cookie.</summary>
    public string Cookie { get => _cookie; set => SetField(ref _cookie, value ?? ""); }

    private long _userId;
    public long UserId { get => _userId; set => SetField(ref _userId, value); }

    private string _username = "";
    public string Username
    {
        get => _username;
        set { if (SetField(ref _username, value ?? "")) RaiseNames(); }
    }

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { if (SetField(ref _displayName, value ?? "")) RaiseNames(); }
    }

    private string _group = "Default";
    public string Group { get => _group; set => SetField(ref _group, string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim()); }

    public string BrowserTrackerId { get; set; } = "";

    private string _color = "";
    /// <summary>Optional colour tag, as hex (e.g. #7B61FF).</summary>
    public string Color
    {
        get => _color;
        set { if (SetField(ref _color, value ?? "")) OnPropertyChanged(nameof(HasColor)); }
    }
    [JsonIgnore] public bool HasColor => !string.IsNullOrEmpty(_color);

    public Dictionary<string, string> Fields { get; set; } = new();
    public DateTime LastUse { get; set; } = DateTime.Now;

    private string _totpSecret = "";
    /// <summary>Base32 2FA secret (stored DPAPI-protected).</summary>
    public string TotpSecret { get => _totpSecret; set => SetField(ref _totpSecret, value ?? ""); }

    private string _proxyUrl = "";
    /// <summary>Optional per-account proxy for this account's web requests and browser window.</summary>
    public string ProxyUrl { get => _proxyUrl; set => SetField(ref _proxyUrl, (value ?? "").Trim()); }

    private string _fflags = "";
    /// <summary>Per-account ClientAppSettings JSON, merged over the global flags at launch.</summary>
    public string FFlags { get => _fflags; set => SetField(ref _fflags, value ?? ""); }

    private bool _autoRejoin;
    public bool AutoRejoin { get => _autoRejoin; set => SetField(ref _autoRejoin, value); }

    private bool _isFavorite;
    /// <summary>Pinned accounts float to the top of their group.</summary>
    public bool IsFavorite { get => _isFavorite; set => SetField(ref _isFavorite, value); }

    private string _alias = "";
    public string Alias
    {
        get => _alias;
        set { if (SetField(ref _alias, value ?? "")) RaiseNames(); }
    }

    private string _description = "";
    public string Description { get => _description; set => SetField(ref _description, value ?? ""); }

    // ---- session health (persisted so the health panel survives a restart) ----

    /// <summary>When the account was added to the manager; null for accounts from before v2.0.</summary>
    public DateTime? AddedUtc { get; set; }

    private DateTime? _cookieUpdatedUtc;
    /// <summary>When the stored cookie last changed (added, rotated, refreshed, restored).</summary>
    public DateTime? CookieUpdatedUtc
    {
        get => _cookieUpdatedUtc;
        set { if (SetField(ref _cookieUpdatedUtc, value)) RaiseHealth(); }
    }

    private DateTime? _lastValidatedUtc;
    /// <summary>Last time Roblox gave a definite answer about this cookie.</summary>
    public DateTime? LastValidatedUtc
    {
        get => _lastValidatedUtc;
        set { if (SetField(ref _lastValidatedUtc, value)) RaiseHealth(); }
    }

    private DateTime? _cookieRejectedUtc;
    /// <summary>Set when Roblox rejected the cookie; cleared as soon as it is accepted again.</summary>
    public DateTime? CookieRejectedUtc
    {
        get => _cookieRejectedUtc;
        set
        {
            if (!SetField(ref _cookieRejectedUtc, value)) return;
            OnPropertyChanged(nameof(IsValid));
            RaiseHealth();
        }
    }

    // ================================================================ runtime

    /// <summary>
    /// The stored (encrypted) cookie when this Windows user cannot decrypt it — the store was copied
    /// from another account or PC. Kept verbatim so saving never overwrites the real value with "".
    /// </summary>
    [JsonIgnore] internal string? UnreadableCookie { get; set; }

    /// <summary>Same as <see cref="UnreadableCookie"/>, for the 2FA secret.</summary>
    [JsonIgnore] internal string? UnreadableTotp { get; set; }

    /// <summary>False once Roblox has definitely rejected the cookie (or it cannot be decrypted here).</summary>
    [JsonIgnore]
    public bool IsValid
    {
        get => _cookieRejectedUtc == null && UnreadableCookie == null;
        set
        {
            if (value == IsValid) return;
            CookieRejectedUtc = value ? null : DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Records a definite verdict from Roblox. Transport failures (timeouts, 429, 5xx) are not
    /// verdicts and must not call this — they say nothing about the cookie.
    /// </summary>
    public void MarkValidated(bool accepted)
    {
        LastValidatedUtc = DateTime.UtcNow;
        IsValid = accepted;
    }

    /// <summary>Replaces the cookie and stamps when it happened.</summary>
    public void ReplaceCookie(string cookie)
    {
        if (string.IsNullOrEmpty(cookie) || cookie == _cookie) return;
        Cookie = cookie;
        CookieUpdatedUtc = DateTime.UtcNow;
    }

    private long _robux = -1;
    [JsonIgnore] public long Robux { get => _robux; set { if (SetField(ref _robux, value)) OnPropertyChanged(nameof(RobuxDisplay)); } }
    [JsonIgnore] public string RobuxDisplay => _robux < 0 ? "—" : _robux.ToString("N0");

    private long _rap = -1;
    [JsonIgnore] public long Rap { get => _rap; set { if (SetField(ref _rap, value)) OnPropertyChanged(nameof(RapDisplay)); } }
    [JsonIgnore] public string RapDisplay => _rap < 0 ? "—" : _rap.ToString("N0");

    private bool _isPremium;
    [JsonIgnore] public bool IsPremium { get => _isPremium; set => SetField(ref _isPremium, value); }

    private string _presence = PresenceStatus.Offline;
    /// <summary>Canonical presence, see <see cref="PresenceStatus"/>.</summary>
    [JsonIgnore]
    public string Presence
    {
        get => _presence;
        set
        {
            if (!SetField(ref _presence, value ?? PresenceStatus.Offline)) return;
            OnPropertyChanged(nameof(PresenceText));
            OnPropertyChanged(nameof(IsOnline));
        }
    }

    [JsonIgnore] public string PresenceText => PresenceStatus.Label(_presence);
    [JsonIgnore] public bool IsOnline => PresenceStatus.IsOnline(_presence);

    private string _lastLocation = "";
    /// <summary>Game name Roblox reports for the current session (empty when hidden or not in game).</summary>
    [JsonIgnore]
    public string LastLocation { get => _lastLocation; set { if (SetField(ref _lastLocation, value ?? "")) OnPropertyChanged(nameof(StatusLine)); } }

    /// <summary>Presence plus the game, when Roblox shares it.</summary>
    [JsonIgnore]
    public string StatusLine => _presence == PresenceStatus.InGame && _lastLocation.Length > 0
        ? L.T("Presence.InGameAt", _lastLocation)
        : PresenceText;

    private long _placeId;
    [JsonIgnore] public long PlaceId { get => _placeId; set { if (SetField(ref _placeId, value)) RaisePlace(); } }

    private long _rootPlaceId;
    [JsonIgnore] public long RootPlaceId { get => _rootPlaceId; set { if (SetField(ref _rootPlaceId, value)) RaisePlace(); } }

    private string? _gameId;
    /// <summary>Job id of the server the account is in, when Roblox shares it.</summary>
    [JsonIgnore] public string? GameId { get => _gameId; set => SetField(ref _gameId, value); }

    /// <summary>
    /// When Roblox last reported this account's presence. Older than <see cref="Services.PresenceService.LastFetchedUtc"/>
    /// means the account was missing from the last answer, so <see cref="Presence"/> may be stale.
    /// </summary>
    [JsonIgnore] public DateTime PresenceFetchedUtc { get; set; }

    /// <summary>True when the account is in a sub-place rather than the experience's start place.</summary>
    [JsonIgnore] public bool IsSubPlace => PlaceId > 0 && RootPlaceId > 0 && PlaceId != RootPlaceId;

    [JsonIgnore] public string SubPlaceTip => IsSubPlace ? L.T("Presence.SubPlaceTip", PlaceId, RootPlaceId) : "";

    private string? _thumbnailUrl;
    [JsonIgnore] public string? ThumbnailUrl { get => _thumbnailUrl; set => SetField(ref _thumbnailUrl, value); }

    private bool _isBusy;
    [JsonIgnore] public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    private bool _isChecked;
    /// <summary>Ticked in the list for bulk actions.</summary>
    [JsonIgnore] public bool IsChecked { get => _isChecked; set => SetField(ref _isChecked, value); }

    private int _runningClients;
    /// <summary>How many Roblox clients the manager currently tracks for this account.</summary>
    [JsonIgnore]
    public int RunningClients
    {
        get => _runningClients;
        set { if (SetField(ref _runningClients, value)) OnPropertyChanged(nameof(IsRunning)); }
    }
    [JsonIgnore] public bool IsRunning => _runningClients > 0;

    // ---- playtime (pre-formatted by PlaytimeService so the model has no service dependency) ----

    private string _playtimeTotalText = "—";
    [JsonIgnore] public string PlaytimeTotalText { get => _playtimeTotalText; set => SetField(ref _playtimeTotalText, value); }

    private string _playtime7dText = "—";
    [JsonIgnore] public string Playtime7dText { get => _playtime7dText; set => SetField(ref _playtime7dText, value); }

    private string _lastPlayedText = "";
    [JsonIgnore] public string LastPlayedText { get => _lastPlayedText; set => SetField(ref _lastPlayedText, value); }

    private TimeSpan _playtimeTotal;
    [JsonIgnore] public TimeSpan PlaytimeTotal { get => _playtimeTotal; set => SetField(ref _playtimeTotal, value); }

    private bool _hasPlaytime;
    [JsonIgnore] public bool HasPlaytime { get => _hasPlaytime; set => SetField(ref _hasPlaytime, value); }

    // ================================================================ derived

    /// <summary>Alias when set, otherwise the username.</summary>
    [JsonIgnore]
    public string DisplayNameOrUser => !string.IsNullOrEmpty(Alias) ? Alias
                                      : !string.IsNullOrEmpty(Username) ? Username
                                      : L.T("Common.Unknown");

    /// <summary>The second line under the name: "@username" when an alias hides it, else the display name if it differs.</summary>
    [JsonIgnore]
    public string SecondaryName
    {
        get
        {
            if (!string.IsNullOrEmpty(Alias) && !string.IsNullOrEmpty(Username)) return "@" + Username;
            if (!string.IsNullOrEmpty(DisplayName) && !string.Equals(DisplayName, Username, StringComparison.Ordinal)) return DisplayName;
            return string.IsNullOrEmpty(Username) ? "" : "@" + Username;
        }
    }

    [JsonIgnore]
    public string Initials
    {
        get
        {
            var s = string.IsNullOrEmpty(Username) ? Alias : Username;
            return string.IsNullOrEmpty(s) ? "?" : s.Substring(0, 1).ToUpperInvariant();
        }
    }

    /// <summary>"Ok", "Invalid", "Unchecked", "Stale" or "Old" — drives the health badge and the Overview panel.</summary>
    [JsonIgnore]
    public string Health
    {
        get
        {
            if (!IsValid) return "Invalid";
            if (_lastValidatedUtc == null) return "Unchecked";
            if (DateTime.UtcNow - _lastValidatedUtc.Value > TimeSpan.FromDays(14)) return "Stale";
            var since = _cookieUpdatedUtc ?? AddedUtc;
            if (since != null && DateTime.UtcNow - since.Value > TimeSpan.FromDays(365)) return "Old";
            return "Ok";
        }
    }

    [JsonIgnore] public bool NeedsAttention => Health is "Invalid" or "Stale" or "Old";

    [JsonIgnore]
    public string HealthText => Health switch
    {
        "Invalid" => L.T("Health.Invalid"),
        "Unchecked" => L.T("Health.Unchecked"),
        "Stale" => L.T("Health.Stale", (int)(DateTime.UtcNow - _lastValidatedUtc!.Value).TotalDays),
        "Old" => L.T("Health.Old"),
        _ => L.T("Health.Ok"),
    };

    /// <summary>Age of the stored session in whole days, or null when unknown.</summary>
    [JsonIgnore]
    public int? CookieAgeDays
    {
        get
        {
            var since = _cookieUpdatedUtc ?? AddedUtc;
            return since == null ? null : Math.Max(0, (int)(DateTime.UtcNow - since.Value).TotalDays);
        }
    }

    [JsonIgnore]
    public string CookieAgeText => CookieAgeDays is { } d ? L.N("Health.DaysAgo", d) : L.T("Common.Unknown");

    [JsonIgnore]
    public string LastValidatedText => _lastValidatedUtc is { } t ? RelativeTime(t) : L.T("Health.Never");

    /// <summary>Short relative time ("just now", "5 min ago", "3 days ago").</summary>
    public static string RelativeTime(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return L.T("Time.JustNow");
        if (span.TotalHours < 1) return L.N("Time.MinutesAgo", (long)span.TotalMinutes);
        if (span.TotalDays < 1) return L.N("Time.HoursAgo", (long)span.TotalHours);
        return L.N("Time.DaysAgo", (long)span.TotalDays);
    }

    /// <summary>Readable fallback for any control that shows the raw object.</summary>
    public override string ToString() => DisplayNameOrUser;

    /// <summary>Re-raises every text property after a language switch.</summary>
    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(PresenceText));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(SubPlaceTip));
        OnPropertyChanged(nameof(DisplayNameOrUser));
        RaiseHealth();
    }

    public void RaiseIdentityChanged() => RaiseNames();

    private void RaiseNames()
    {
        OnPropertyChanged(nameof(DisplayNameOrUser));
        OnPropertyChanged(nameof(SecondaryName));
        OnPropertyChanged(nameof(Initials));
    }

    private void RaisePlace()
    {
        OnPropertyChanged(nameof(IsSubPlace));
        OnPropertyChanged(nameof(SubPlaceTip));
    }

    /// <summary>Health depends on the clock too, so the Overview refreshes it periodically.</summary>
    public void RaiseHealth()
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(CookieAgeDays));
        OnPropertyChanged(nameof(CookieAgeText));
        OnPropertyChanged(nameof(LastValidatedText));
    }

    public string GetField(string key) => Fields.TryGetValue(key, out var v) ? v : "";
    public void SetFieldValue(string key, string value) => Fields[key] = value;
}
