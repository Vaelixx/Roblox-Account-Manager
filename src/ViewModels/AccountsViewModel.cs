using System.Collections;
using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public class AccountsViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;

    public AccountStore Store => _store;

    private Account? _selected;
    public Account? Selected
    {
        get => _selected;
        set { if (SetField(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); SyncGroupSelection(); } }
    }

    public bool HasSelection => _selected != null;

    // ---- group dropdown ----
    private const string NewGroupSentinel = "＋  New group…";
    public System.Collections.ObjectModel.ObservableCollection<string> GroupOptions { get; } = new();

    private bool _suppressGroupApply;
    private string? _selectedGroupOption;
    public string? SelectedGroupOption
    {
        get => _selectedGroupOption;
        set
        {
            if (!SetField(ref _selectedGroupOption, value)) return;
            if (_suppressGroupApply || value == null) return;
            if (value == NewGroupSentinel) PromptNewGroup();
            else ApplyGroup(value);
        }
    }

    private void RefreshGroupOptions()
    {
        GroupOptions.Clear();
        var groups = _store.Accounts.Select(a => a.Group).Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct().OrderBy(g => g).ToList();
        if (!groups.Contains("Default")) GroupOptions.Add("Default");
        foreach (var g in groups) GroupOptions.Add(g);
        GroupOptions.Add(NewGroupSentinel);
    }

    private void SyncGroupSelection()
    {
        _suppressGroupApply = true;
        RefreshGroupOptions();
        _selectedGroupOption = _selected?.Group;
        OnPropertyChanged(nameof(SelectedGroupOption));
        _suppressGroupApply = false;
    }

    private void PromptNewGroup()
    {
        string? g = DialogService.Prompt("New group", "Name the new group");
        if (string.IsNullOrWhiteSpace(g)) { SyncGroupSelection(); return; }
        ApplyGroup(g.Trim());
    }

    private void ApplyGroup(string g)
    {
        if (_selected == null || _selected.Group == g) { SyncGroupSelection(); return; }
        var acc = _selected;
        acc.Group = g;
        int idx = _store.Accounts.IndexOf(acc);
        if (idx >= 0) _store.Accounts.RemoveAt(idx);
        _store.Accounts.Add(acc);
        Selected = acc;              // re-select and resync the dropdown
        _store.Save();
        _main.SetStatus($"Moved {acc.DisplayNameOrUser} to '{g}'.");
    }

    public bool MaskUsernames => SettingsService.Current.HideUsernames;
    public void RefreshMask() => OnPropertyChanged(nameof(MaskUsernames));

    private string _placeIdText = "";
    public string PlaceIdText
    {
        get => _placeIdText;
        set { if (SetField(ref _placeIdText, value)) LookupPlaceDebounced(); }
    }

    private string _gameName = "";
    public string GameName { get => _gameName; set { SetField(ref _gameName, value); OnPropertyChanged(nameof(HasGameInfo)); } }

    private string _gameCreator = "";
    public string GameCreator { get => _gameCreator; set => SetField(ref _gameCreator, value); }

    private string? _gameIcon;
    public string? GameIcon { get => _gameIcon; set => SetField(ref _gameIcon, value); }

    public bool HasGameInfo => !string.IsNullOrEmpty(_gameName);

    private CancellationTokenSource? _placeLookupCts;

    private void LookupPlaceDebounced()
    {
        _placeLookupCts?.Cancel();
        var digits = new string(_placeIdText.Where(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out long placeId) || placeId <= 0)
        {
            GameName = ""; GameCreator = ""; GameIcon = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _placeLookupCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                if (cts.Token.IsCancellationRequested) return;

                string cookie = _store.Accounts.FirstOrDefault(a => a.IsValid)?.Cookie ?? "";
                var info = await RobloxApi.GetPlaceInfoAsync(cookie, placeId);
                if (cts.Token.IsCancellationRequested || info == null) return;

                string? icon = await RobloxApi.GetGameIconAsync(info.UniverseId);
                if (cts.Token.IsCancellationRequested) return;

                App.Current.Dispatcher.Invoke(() =>
                {
                    GameName = info.Name;
                    GameCreator = string.IsNullOrEmpty(info.Creator) ? "" : $"by {info.Creator}";
                    GameIcon = icon;
                });
            }
            catch { }
        });
    }

    // ---- saved places ----
    public System.Collections.ObjectModel.ObservableCollection<SavedPlace> SavedPlaces { get; } = new();

    private bool _savedPlacesOpen;
    public bool SavedPlacesOpen { get => _savedPlacesOpen; set => SetField(ref _savedPlacesOpen, value); }

    private string _jobIdText = "";
    public string JobIdText { get => _jobIdText; set => SetField(ref _jobIdText, value); }

    private string _followText = "";
    public string FollowText { get => _followText; set => SetField(ref _followText, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }

    // Commands
    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public AsyncRelayCommand JoinServerCommand { get; }
    public AsyncRelayCommand ShuffleJoinCommand { get; }
    public AsyncRelayCommand FollowCommand { get; }
    public AsyncRelayCommand RefreshAllCommand { get; }
    public RelayCommand CopyCookieCommand { get; }
    public RelayCommand SaveDetailsCommand { get; }
    public RelayCommand SetGroupCommand { get; }
    public RelayCommand BrowseServersCommand { get; }
    public AsyncRelayCommand OpenBrowserCommand { get; }
    public RelayCommand OpenProfileCommand { get; }
    public AsyncRelayCommand OpenAppCommand { get; }
    public RelayCommand SavePlaceCommand { get; }
    public RelayCommand RemovePlaceCommand { get; }
    public RelayCommand ApplySavedPlaceCommand { get; }

    public AccountsViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;
        PlaceIdText = SettingsService.Current.DefaultPlaceId > 0 ? SettingsService.Current.DefaultPlaceId.ToString() : "";
        foreach (var p in SettingsService.Current.SavedPlaces) SavedPlaces.Add(p);

        AddCommand = new AsyncRelayCommand(AddAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        RemoveCommand = new RelayCommand(p => Remove(p as IList));
        LaunchCommand = new AsyncRelayCommand(p => LaunchAsync(p as IList));
        JoinServerCommand = new AsyncRelayCommand(p => JoinServerAsync(p as IList));
        ShuffleJoinCommand = new AsyncRelayCommand(p => ShuffleJoinAsync(p as IList));
        FollowCommand = new AsyncRelayCommand(p => FollowAsync(p as IList));
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        CopyCookieCommand = new RelayCommand(_ => CopyCookie());
        SaveDetailsCommand = new RelayCommand(_ => SaveDetails());
        SetGroupCommand = new RelayCommand(p => SetGroup(p as IList));
        BrowseServersCommand = new RelayCommand(_ => BrowseServers());
        OpenBrowserCommand = new AsyncRelayCommand(OpenBrowserAsync);
        OpenProfileCommand = new RelayCommand(_ => { if (_selected != null) BrowserService.OpenProfile(_selected); });
        OpenAppCommand = new AsyncRelayCommand(OpenAppAsync);
        SavePlaceCommand = new RelayCommand(_ => SavePlace());
        RemovePlaceCommand = new RelayCommand(p => RemovePlace(p as SavedPlace));
        ApplySavedPlaceCommand = new RelayCommand(p => ApplySavedPlace(p as SavedPlace));
    }

    private async Task OpenBrowserAsync()
    {
        if (_selected == null) { _main.SetStatus("Select an account first."); return; }
        Busy = true; _main.SetStatus($"Opening {_selected.DisplayNameOrUser} in a browser…");
        var r = await BrowserService.OpenLoggedInAsync(_selected);
        Busy = false;

        if (!r.Success && r.Message == "no-chromium")
        {
            if (DialogService.Confirm("Chromium required",
                "Opening an account in a browser uses a private, portable Chromium (downloaded once, ~330 MB, kept separate from your normal browser). Download it now?"))
            {
                if (DialogService.ShowChromiumDownload())
                {
                    _main.SetStatus("Chromium ready — opening account…");
                    Busy = true;
                    var r2 = await BrowserService.OpenLoggedInAsync(_selected);
                    Busy = false;
                    _main.SetStatus(r2.Message);
                }
                else _main.SetStatus("Chromium download cancelled.");
            }
            return;
        }

        _main.SetStatus(r.Message);
    }

    private async Task OpenAppAsync()
    {
        if (_selected == null) { _main.SetStatus("Select an account first."); return; }
        Busy = true; _main.SetStatus($"Opening Roblox as {_selected.DisplayNameOrUser}…");
        var r = await LauncherService.OpenRobloxAppAsync(_selected);
        _store.Save();
        Busy = false;
        _main.SetStatus(r.Success ? $"Opened Roblox as {_selected.DisplayNameOrUser}." : r.Message);
    }

    private static List<Account> Sel(IList? list)
    {
        if (list == null) return new();
        return list.Cast<Account>().ToList();
    }

    private async Task AddAsync()
    {
        var added = DialogService.ShowAddAccount(_store);
        if (added == null) return;
        Selected = added;
        _main.SetStatus($"Added {added.DisplayNameOrUser}.");
        await _store.RefreshLiveDataAsync(new[] { added });
    }

    private async Task ImportAsync()
    {
        var text = DialogService.ShowImport();
        if (string.IsNullOrWhiteSpace(text)) return;
        Busy = true;
        var (added, failed) = await _store.ImportManyAsync(text, s => _main.SetStatus(s));
        Busy = false;
        _main.SetStatus($"Import complete — {added} added, {failed} failed.");
        await _store.RefreshLiveDataAsync();
    }

    private void Remove(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select an account first."); return; }
        string names = sel.Count == 1 ? sel[0].DisplayNameOrUser : $"{sel.Count} accounts";
        if (!DialogService.Confirm("Remove account", $"Remove {names}? This only deletes it from the manager, not from Roblox."))
            return;
        foreach (var a in sel) _store.Remove(a);
        _main.SetStatus($"Removed {names}.");
    }

    private bool TryPlaceId(out long placeId)
    {
        placeId = 0;
        var t = new string(PlaceIdText.Where(char.IsDigit).ToArray());
        if (!long.TryParse(t, out placeId) || placeId <= 0)
        {
            _main.SetStatus("Enter a valid Place ID first.");
            return false;
        }
        SettingsService.Current.DefaultPlaceId = placeId;
        SettingsService.Save();
        return true;
    }

    private async Task LaunchAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select at least one account."); return; }
        if (!TryPlaceId(out long placeId)) return;
        // A single Launch button: if a Job ID is filled in, join that specific server automatically.
        string? job = string.IsNullOrWhiteSpace(JobIdText) ? null : JobIdText.Trim();
        await LaunchSequential(sel, placeId, job, 0);
    }

    private async Task JoinServerAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select at least one account."); return; }
        if (!TryPlaceId(out long placeId)) return;
        string job = JobIdText.Trim();
        if (string.IsNullOrEmpty(job)) { _main.SetStatus("Enter a Job ID, or use Smart Join."); return; }
        await LaunchSequential(sel, placeId, job, 0);
    }

    private async Task ShuffleJoinAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select at least one account."); return; }
        if (!TryPlaceId(out long placeId)) return;
        _main.SetStatus("Finding a good server…");
        var s = SettingsService.Current;
        string job = await RobloxApi.GetRandomJobIdAsync(placeId, s.ShuffleLowestServer, s.ShufflePageCount);
        if (string.IsNullOrEmpty(job)) { _main.SetStatus("No joinable public servers found."); return; }
        await LaunchSequential(sel, placeId, job, 0);
    }

    private async Task FollowAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select at least one account."); return; }
        string user = FollowText.Trim();
        if (string.IsNullOrEmpty(user)) { _main.SetStatus("Enter a username to follow."); return; }
        _main.SetStatus($"Looking up {user}…");
        long id = await RobloxApi.GetUserIdAsync(user);
        if (id <= 0) { _main.SetStatus($"Could not find user '{user}'."); return; }
        await LaunchSequential(sel, 0, null, id);
    }

    private async Task LaunchSequential(List<Account> accounts, long placeId, string? job, long followId)
    {
        if (followId == 0 && placeId > 0 && !RequirementsService.IsRobloxInstalled())
        {
            Busy = false;
            DialogService.OfferDownload("Roblox not found",
                "The Roblox player isn't installed, so games can't be launched. Download Roblox now?",
                "https://www.roblox.com/download");
            return;
        }

        Busy = true;
        int delay = Math.Max(0, SettingsService.Current.AccountJoinDelay);
        int ok = 0;
        string? firstError = null;
        for (int i = 0; i < accounts.Count; i++)
        {
            var a = accounts[i];
            a.IsBusy = true;
            _main.SetStatus($"Launching {a.DisplayNameOrUser} ({i + 1}/{accounts.Count})…");
            var r = await LauncherService.LaunchAsync(a, placeId, job, followId);
            a.IsBusy = false;
            if (r.Success) ok++;
            else firstError ??= $"{a.DisplayNameOrUser}: {r.Message}";
            _store.Save();
            if (i < accounts.Count - 1 && delay > 0)
            {
                for (int s = delay; s > 0; s--)
                {
                    _main.SetStatus($"Next launch in {s}s…");
                    await Task.Delay(1000);
                }
            }
        }
        Busy = false;

        if (firstError != null)
        {
            _main.SetStatus(firstError);
            DialogService.Info("Launch failed", firstError);
        }
        else
        {
            _main.SetStatus($"Launched {ok} client(s).");
        }
    }

    private async Task RefreshAllAsync()
    {
        if (_store.Accounts.Count == 0) { _main.SetStatus("No accounts to refresh."); return; }
        Busy = true; _main.SetStatus("Refreshing account data…");
        await _store.RefreshLiveDataAsync();
        Busy = false; _main.SetStatus("Account data refreshed.");
    }

    private void CopyCookie()
    {
        if (_selected == null) { _main.SetStatus("Select an account first."); return; }
        try { Clipboard.SetText(_selected.Cookie); _main.SetStatus("Cookie copied to clipboard."); }
        catch { _main.SetStatus("Could not access the clipboard."); }
    }

    private void SaveDetails()
    {
        _store.Save();
        _main.SetStatus("Saved.");
    }

    private void SetGroup(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0 && _selected != null) sel.Add(_selected);
        if (sel.Count == 0) { _main.SetStatus("Select an account first."); return; }
        string current = sel[0].Group;
        string? g = DialogService.Prompt("Set group", "Group name", current);
        if (g == null) return;
        g = string.IsNullOrWhiteSpace(g) ? "Default" : g.Trim();
        foreach (var a in sel) a.Group = g;
        // Remove + re-add so the grouped CollectionView re-buckets these rows.
        foreach (var a in sel)
        {
            int idx = _store.Accounts.IndexOf(a);
            if (idx >= 0) _store.Accounts.RemoveAt(idx);
            _store.Accounts.Add(a);
        }
        Selected = sel[0];
        _store.Save();
        _main.SetStatus($"Moved {sel.Count} account(s) to '{g}'.");
    }

    private void BrowseServers()
    {
        if (!TryPlaceId(out long placeId)) return;
        _main.OpenServersFor(placeId);
    }

    // ---- saved places ----
    private void SavePlace()
    {
        var digits = new string(PlaceIdText.Where(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out long placeId) || placeId <= 0)
        {
            _main.SetStatus("Enter a valid Place ID before saving it.");
            return;
        }

        string? name = DialogService.Prompt("Save place", "Name for this place", GameName);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        var existing = SavedPlaces.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) SavedPlaces.Remove(existing);
        SavedPlaces.Add(new SavedPlace { Name = name, PlaceId = placeId });

        PersistSavedPlaces();
        _main.SetStatus($"Saved '{name}' ({placeId}).");
    }

    private void RemovePlace(SavedPlace? place)
    {
        if (place == null) return;
        SavedPlaces.Remove(place);
        PersistSavedPlaces();
        _main.SetStatus($"Removed saved place '{place.Name}'.");
    }

    private void ApplySavedPlace(SavedPlace? place)
    {
        if (place == null) return;
        SavedPlacesOpen = false;
        PlaceIdText = place.PlaceId.ToString();   // setter kicks off the debounced game lookup
        _main.SetStatus($"Place set to '{place.Name}'.");
    }

    private void PersistSavedPlaces()
    {
        SettingsService.Current.SavedPlaces = SavedPlaces.ToList();
        SettingsService.Save();
    }
}
