using System.Collections.ObjectModel;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public class ServerBrowserViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;

    public ObservableCollection<GameServer> Servers { get; } = new();

    private string _placeIdText = "";
    public string PlaceIdText { get => _placeIdText; set => SetField(ref _placeIdText, value); }

    private string _placeName = "";
    public string PlaceName { get => _placeName; set => SetField(ref _placeName, value); }

    private bool _isSubPlace;
    /// <summary>True when the browsed Place ID is a sub-place (teleport/co-edit) of its universe, not the root game.</summary>
    public bool IsSubPlace { get => _isSubPlace; set => SetField(ref _isSubPlace, value); }

    private string _subPlaceTip = "";
    public string SubPlaceTip { get => _subPlaceTip; set => SetField(ref _subPlaceTip, value); }

    private GameServer? _selected;
    public GameServer? Selected { get => _selected; set => SetField(ref _selected, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }

    public int ServerCount => Servers.Count;

    private bool _sortByPing;
    /// <summary>false = sort by player count (busiest first); true = sort by ping (lowest first).</summary>
    public bool SortByPing
    {
        get => _sortByPing;
        set { if (SetField(ref _sortByPing, value)) { OnPropertyChanged(nameof(SortModeLabel)); ApplySort(); } }
    }
    public string SortModeLabel => _sortByPing ? "Sort: Ping" : "Sort: Players";

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand JoinCommand { get; }
    public RelayCommand CopyJobIdCommand { get; }
    public RelayCommand ToggleSortCommand { get; }
    public RelayCommand CopyPlaceIdCommand { get; }

    public ServerBrowserViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        JoinCommand = new AsyncRelayCommand(JoinAsync);
        CopyJobIdCommand = new RelayCommand(_ => CopyJobId());
        ToggleSortCommand = new RelayCommand(_ => SortByPing = !SortByPing);
        CopyPlaceIdCommand = new RelayCommand(_ => CopyPlaceId());
    }

    private void ApplySort()
    {
        var ordered = _sortByPing
            ? Servers.OrderBy(s => s.Ping <= 0 ? int.MaxValue : s.Ping).ToList()
            : Servers.OrderByDescending(s => s.Playing).ToList();
        Servers.Clear();
        foreach (var s in ordered) Servers.Add(s);
    }

    private void CopyPlaceId()
    {
        var t = new string(PlaceIdText.Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(t)) { _main.SetStatus("No Place ID to copy."); return; }
        try { System.Windows.Clipboard.SetText(t); _main.SetStatus($"Place ID {t} copied."); }
        catch { _main.SetStatus("Could not access the clipboard."); }
    }

    public async Task RefreshAsync()
    {
        var t = new string(PlaceIdText.Where(char.IsDigit).ToArray());
        if (!long.TryParse(t, out long placeId) || placeId <= 0)
        {
            _main.SetStatus("Enter a valid Place ID to browse servers.");
            return;
        }

        Busy = true;
        Servers.Clear();
        OnPropertyChanged(nameof(ServerCount));
        _main.SetStatus("Loading servers…");

        try
        {
            IsSubPlace = false;
            SubPlaceTip = "";
            var cookie = _store.Accounts.FirstOrDefault(a => a.IsValid)?.Cookie;
            if (cookie != null)
            {
                var info = await RobloxApi.GetPlaceInfoAsync(cookie, placeId);
                PlaceName = info?.Name ?? $"Place {placeId}";
                if (info != null && info.IsSubPlace)
                {
                    IsSubPlace = true;
                    SubPlaceTip = $"Place {info.PlaceId} is a sub-place of universe {info.UniverseId} (root place {info.RootPlaceId}). Servers here are teleport/co-edit instances, not the main game.";
                }
            }

            var list = await RobloxApi.GetPublicServersAsync(placeId, SettingsService.Current.ShufflePageCount);
            foreach (var s in list) Servers.Add(s);
            ApplySort();

            OnPropertyChanged(nameof(ServerCount));
            _main.SetStatus($"Found {Servers.Count} public server(s).");
        }
        catch (Exception ex)
        {
            // Never leave Busy stuck true (buttons would stay disabled for the session).
            _main.SetStatus($"Could not load servers: {ex.Message}");
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task JoinAsync()
    {
        if (_selected == null) { _main.SetStatus("Select a server first."); return; }
        var acc = _main.Accounts.Selected ?? _store.Accounts.FirstOrDefault(a => a.IsValid);
        if (acc == null) { _main.SetStatus("Select an account on the Accounts page first."); return; }
        var t = new string(PlaceIdText.Where(char.IsDigit).ToArray());
        if (!long.TryParse(t, out long placeId)) return;

        Busy = true;
        _main.SetStatus($"Joining {acc.DisplayNameOrUser} into server {_selected.ShortId}…");
        try
        {
            var r = await LauncherService.LaunchAsync(acc, placeId, _selected.Id);
            _main.SetStatus(r.Success ? $"Launched {acc.DisplayNameOrUser}." : r.Message);
        }
        catch (Exception ex)
        {
            _main.SetStatus($"Join failed: {ex.Message}");
        }
        finally
        {
            Busy = false;
        }
    }

    private void CopyJobId()
    {
        if (_selected == null) return;
        try { System.Windows.Clipboard.SetText(_selected.Id); _main.SetStatus("Job ID copied."); }
        catch { _main.SetStatus("Could not access the clipboard."); }
    }
}
