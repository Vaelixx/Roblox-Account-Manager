using System.Collections.ObjectModel;
using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public class ServerBrowserViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;
    private List<GameServer> _loaded = new();

    public ServerBrowserViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        JoinCommand = new AsyncRelayCommand(p => JoinAsync(p as GameServer ?? _selected));
        CopyJobIdCommand = new RelayCommand(p =>
        {
            if ((p as GameServer ?? _selected) is { } s)
                _main.SetStatus(ClipboardService.CopyText(s.Id) ? L.T("Servers.JobCopied") : L.T("Status.ClipboardBusy"));
        });
        CopyLinkCommand = new RelayCommand(p =>
        {
            if ((p as GameServer ?? _selected) is { } s && _placeId > 0)
                _main.SetStatus(ClipboardService.CopyText($"roblox://experiences/start?placeId={_placeId}&gameInstanceId={s.Id}")
                    ? L.T("Servers.LinkCopied") : L.T("Status.ClipboardBusy"));
        });
    }

    public ObservableCollection<GameServer> Servers { get; } = new();
    public ObservableCollection<Account> Accounts => _store.Accounts;

    public bool MaskUsernames => SettingsService.Current.HideUsernames;

    private string _placeIdText = "";
    public string PlaceIdText { get => _placeIdText; set => SetField(ref _placeIdText, value ?? ""); }

    private long _placeId;

    private string _placeName = "";
    public string PlaceName { get => _placeName; private set { if (SetField(ref _placeName, value)) OnPropertyChanged(nameof(HasPlace)); } }

    private string _placeCreator = "";
    public string PlaceCreator { get => _placeCreator; private set => SetField(ref _placeCreator, value); }

    private string? _placeIcon;
    public string? PlaceIcon { get => _placeIcon; private set => SetField(ref _placeIcon, value); }

    public bool HasPlace => _placeName.Length > 0;

    private bool _isSubPlace;
    public bool IsSubPlace { get => _isSubPlace; private set => SetField(ref _isSubPlace, value); }

    private string _subPlaceTip = "";
    public string SubPlaceTip { get => _subPlaceTip; private set => SetField(ref _subPlaceTip, value); }

    private GameServer? _selected;
    public GameServer? Selected { get => _selected; set => SetField(ref _selected, value); }

    private Account? _joinAccount;
    /// <summary>Account that joins; defaults to the one selected on the Accounts page.</summary>
    public Account? JoinAccount
    {
        get => _joinAccount ?? _main.Accounts.Selected ?? _store.Accounts.FirstOrDefault(a => a.IsValid);
        set => SetField(ref _joinAccount, value);
    }

    /// <summary>The default join account follows the Accounts page until one is picked here.</summary>
    public void OnShown() => OnPropertyChanged(nameof(JoinAccount));

    private bool _busy;
    public bool Busy { get => _busy; private set { if (SetField(ref _busy, value)) OnPropertyChanged(nameof(ShowEmpty)); } }

    private string _sortMode = "Players";
    /// <summary>Players (fullest first) | Empty (emptiest first) | Ping (lowest first)</summary>
    public string SortMode { get => _sortMode; set { if (SetField(ref _sortMode, value ?? "Players")) ApplyView(); } }

    private bool _hideFull = true;
    public bool HideFull { get => _hideFull; set { if (SetField(ref _hideFull, value)) ApplyView(); } }

    public int ServerCount => Servers.Count;
    public int PlayerCount => Servers.Sum(s => s.Playing);
    public bool ShowEmpty => !_busy && Servers.Count == 0;
    public string Summary => _loaded.Count == 0 ? L.T("Servers.Subtitle") : L.N("Servers.Summary", Servers.Count, PlayerCount);

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand JoinCommand { get; }
    public RelayCommand CopyJobIdCommand { get; }
    public RelayCommand CopyLinkCommand { get; }

    public void RefreshLocalized() => OnPropertyChanged(string.Empty);

    private void ApplyView()
    {
        IEnumerable<GameServer> view = _loaded;
        if (_hideFull) view = view.Where(s => s.Playing < s.MaxPlayers);
        view = _sortMode switch
        {
            "Ping" => view.OrderBy(s => s.Ping <= 0 ? int.MaxValue : s.Ping),
            "Empty" => view.OrderBy(s => s.Playing),
            _ => view.OrderByDescending(s => s.Playing),
        };

        var keep = _selected;
        Servers.Clear();
        foreach (var s in view) Servers.Add(s);
        if (keep != null && Servers.Contains(keep)) Selected = keep;

        OnPropertyChanged(nameof(ServerCount));
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    private static long ParsePlace(string text) => JoinLinks.ParsePlaceId(text);

    public async Task RefreshAsync()
    {
        long placeId = ParsePlace(PlaceIdText);
        if (placeId <= 0)
        {
            _main.SetStatus(L.T("Launch.NeedPlace"));
            return;
        }

        _placeId = placeId;
        Busy = true;
        _loaded.Clear();
        ApplyView();
        _main.SetStatus(L.T("Servers.Loading"));

        try
        {
            IsSubPlace = false;
            SubPlaceTip = "";
            var cookie = _store.Accounts.FirstOrDefault(a => a.IsValid)?.Cookie ?? "";
            var info = await RobloxApi.GetPlaceInfoAsync(cookie, placeId);
            PlaceName = info?.Name ?? L.T("Place.Fallback", placeId);
            PlaceCreator = string.IsNullOrEmpty(info?.Creator) ? "" : L.T("Launch.By", info!.Creator);
            PlaceIcon = info != null ? await RobloxApi.GetGameIconAsync(info.UniverseId) : null;
            if (info is { IsSubPlace: true })
            {
                IsSubPlace = true;
                SubPlaceTip = L.T("Servers.SubPlaceTip", info.PlaceId, info.RootPlaceId);
            }

            _loaded = await RobloxApi.GetPublicServersAsync(placeId, SettingsService.Current.ShufflePageCount);
            ApplyView();
            _main.SetStatus(L.N("Servers.Found", _loaded.Count));
        }
        catch (Exception ex)
        {
            _main.SetStatus(L.T("Servers.LoadFailed", ex.Message));
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task JoinAsync(GameServer? server)
    {
        if (server == null) { _main.SetStatus(L.T("Servers.PickServer")); return; }
        var acc = JoinAccount;
        if (acc == null) { _main.SetStatus(L.T("Status.SelectFirst")); return; }
        if (_placeId <= 0) return;

        _main.SetStatus(L.T("Launch.Launching", acc.DisplayNameOrUser));
        var r = await LauncherService.LaunchAsync(acc, _placeId, server.Id);
        _main.SetStatus(r.Success ? L.T("Status.Launched", acc.DisplayNameOrUser) : r.Message);
        if (!r.Success) ToastService.Error(L.T("Launch.FailedTitle"), r.Message);
    }
}
