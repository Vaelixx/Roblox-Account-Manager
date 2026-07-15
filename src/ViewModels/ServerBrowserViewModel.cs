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

    private GameServer? _selected;
    public GameServer? Selected { get => _selected; set => SetField(ref _selected, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }

    public int ServerCount => Servers.Count;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand JoinCommand { get; }
    public RelayCommand CopyJobIdCommand { get; }

    public ServerBrowserViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        JoinCommand = new AsyncRelayCommand(JoinAsync);
        CopyJobIdCommand = new RelayCommand(_ => CopyJobId());
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

        var cookie = _store.Accounts.FirstOrDefault(a => a.IsValid)?.Cookie;
        if (cookie != null)
        {
            var info = await RobloxApi.GetPlaceInfoAsync(cookie, placeId);
            PlaceName = info?.Name ?? $"Place {placeId}";
        }

        var list = await RobloxApi.GetPublicServersAsync(placeId, SettingsService.Current.ShufflePageCount);
        foreach (var s in list.OrderByDescending(s => s.Playing)) Servers.Add(s);

        OnPropertyChanged(nameof(ServerCount));
        Busy = false;
        _main.SetStatus($"Found {Servers.Count} public server(s).");
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
        var r = await LauncherService.LaunchAsync(acc, placeId, _selected.Id);
        Busy = false;
        _main.SetStatus(r.Success ? $"Launched {acc.DisplayNameOrUser}." : r.Message);
    }

    private void CopyJobId()
    {
        if (_selected == null) return;
        try { System.Windows.Clipboard.SetText(_selected.Id); _main.SetStatus("Job ID copied."); }
        catch { }
    }
}
