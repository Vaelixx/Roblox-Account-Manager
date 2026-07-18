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

    // ---- scale management (#28): bulk colour tags, duplicate scan, export ----

    /// <summary>Checked rows if any are ticked, otherwise the single selected account.</summary>
    private System.Collections.Generic.List<Account> CheckedOrSelected()
    {
        var ticked = _store.Accounts.Where(a => a.IsChecked).ToList();
        if (ticked.Count > 0) return ticked;
        return _selected != null
            ? new System.Collections.Generic.List<Account> { _selected }
            : new System.Collections.Generic.List<Account>();
    }

    private void SetColor(string? hex)
    {
        var targets = CheckedOrSelected();
        if (targets.Count == 0) { _main.SetStatus("Check or select accounts to colour first."); return; }
        foreach (var a in targets) a.Color = hex ?? "";
        _store.Save();
        _main.SetStatus(string.IsNullOrEmpty(hex)
            ? $"Cleared colour on {targets.Count} account(s)."
            : $"Coloured {targets.Count} account(s).");
    }

    private void Export()
    {
        var ticked = _store.Accounts.Where(a => a.IsChecked).ToList();
        var scope = ticked.Count > 0 ? ticked : _store.Accounts.ToList();
        if (scope.Count == 0) { _main.SetStatus("No accounts to export."); return; }
        string? path = DialogService.SaveFile("Export accounts",
            "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            $"accounts-{System.DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path == null) return;
        try
        {
            bool json = path.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase);
            string content = json
                ? ScaleService.ToJson(scope, includeCookies: false)
                : ScaleService.ToCsv(scope);
            System.IO.File.WriteAllText(path, content);
            _main.SetStatus($"Exported {scope.Count} account(s) → {System.IO.Path.GetFileName(path)}.");
        }
        catch (System.Exception ex) { DialogService.Info("Export failed", ex.Message); }
    }

    private void FindDuplicates()
    {
        var dupes = ScaleService.FindDuplicates(_store.Accounts);
        foreach (var a in _store.Accounts) a.IsChecked = false;
        int n = 0;
        foreach (var grp in dupes)
            foreach (var a in grp) { a.IsChecked = true; n++; }
        if (n == 0)
            DialogService.Info("Duplicate scan", "No duplicate accounts found — every UserId is unique.");
        else
            _main.SetStatus($"{dupes.Count} duplicate set(s) found — {n} accounts checked for review.");
    }

    public bool MaskUsernames => SettingsService.Current.HideUsernames;
    public void RefreshMask() => OnPropertyChanged(nameof(MaskUsernames));

    // View-mode (Card vs Compact) — mirrors the RefreshMask cross-VM pattern; driven by SettingsViewModel.AccountViewMode
    public bool IsCompact => string.Equals(SettingsService.Current.AccountViewMode, "Compact", System.StringComparison.OrdinalIgnoreCase);
    public void RefreshViewMode() => OnPropertyChanged(nameof(IsCompact));

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

    // UniverseId from the most recent place lookup — cached onto a SavedPlace when it's saved.
    private long _lastUniverseId;

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
                    _lastUniverseId = info.UniverseId;
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
    public bool SavedPlacesOpen
    {
        get => _savedPlacesOpen;
        set { if (SetField(ref _savedPlacesOpen, value) && value) _ = LoadSavedPlaceIconsAsync(); }
    }

    private string _jobIdText = "";
    public string JobIdText { get => _jobIdText; set => SetField(ref _jobIdText, value); }

    private string _followText = "";
    public string FollowText { get => _followText; set => SetField(ref _followText, value); }

    private bool _busy;
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }

    // Commands
    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ImportIc3w0lfCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public AsyncRelayCommand JoinServerCommand { get; }
    public AsyncRelayCommand ShuffleJoinCommand { get; }
    public AsyncRelayCommand FollowCommand { get; }
    public AsyncRelayCommand RefreshAllCommand { get; }
    public RelayCommand CopyCookieCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand SaveDetailsCommand { get; }
    public RelayCommand SetGroupCommand { get; }
    public RelayCommand BrowseServersCommand { get; }
    public AsyncRelayCommand OpenBrowserCommand { get; }
    public RelayCommand OpenProfileCommand { get; }
    public AsyncRelayCommand OpenAppCommand { get; }
    public RelayCommand SavePlaceCommand { get; }
    public RelayCommand RemovePlaceCommand { get; }
    public RelayCommand ApplySavedPlaceCommand { get; }
    public RelayCommand SetColorCommand { get; }
    public RelayCommand ClearColorCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand FindDuplicatesCommand { get; }
    public AsyncRelayCommand PingJoinCommand { get; }
    public AsyncRelayCommand ServerHopCommand { get; }
    public AsyncRelayCommand SquadJoinCommand { get; }
    public AsyncRelayCommand InjectScriptCommand { get; }

    public AccountsViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;
        PlaceIdText = SettingsService.Current.DefaultPlaceId > 0 ? SettingsService.Current.DefaultPlaceId.ToString() : "";
        foreach (var p in SettingsService.Current.SavedPlaces) SavedPlaces.Add(p);

        AddCommand = new AsyncRelayCommand(AddAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ImportIc3w0lfCommand = new AsyncRelayCommand(ImportIc3w0lfAsync);
        RemoveCommand = new RelayCommand(p => Remove(p as IList));
        LaunchCommand = new AsyncRelayCommand(p => LaunchAsync(p as IList));
        JoinServerCommand = new AsyncRelayCommand(p => JoinServerAsync(p as IList));
        ShuffleJoinCommand = new AsyncRelayCommand(p => ShuffleJoinAsync(p as IList));
        FollowCommand = new AsyncRelayCommand(p => FollowAsync(p as IList));
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        CopyCookieCommand = new RelayCommand(_ => CopyCookie());
        ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as Account));
        SaveDetailsCommand = new RelayCommand(_ => SaveDetails());
        SetGroupCommand = new RelayCommand(p => SetGroup(p as IList));
        BrowseServersCommand = new RelayCommand(_ => BrowseServers());
        OpenBrowserCommand = new AsyncRelayCommand(OpenBrowserAsync);
        OpenProfileCommand = new RelayCommand(_ => { if (_selected != null) BrowserService.OpenProfile(_selected); });
        OpenAppCommand = new AsyncRelayCommand(OpenAppAsync);
        SavePlaceCommand = new RelayCommand(_ => SavePlace());
        RemovePlaceCommand = new RelayCommand(p => RemovePlace(p as SavedPlace));
        ApplySavedPlaceCommand = new RelayCommand(p => ApplySavedPlace(p as SavedPlace));
        SetColorCommand = new RelayCommand(p => SetColor(p as string));
        ClearColorCommand = new RelayCommand(_ => SetColor(""));
        ExportCommand = new RelayCommand(_ => Export());
        FindDuplicatesCommand = new RelayCommand(_ => FindDuplicates());
        PingJoinCommand = new AsyncRelayCommand(p => PingJoinAsync(p as IList));
        ServerHopCommand = new AsyncRelayCommand(p => ServerHopAsync(p as IList));
        SquadJoinCommand = new AsyncRelayCommand(p => SquadJoinAsync(p as IList));
        InjectScriptCommand = new AsyncRelayCommand(p => InjectScriptAsync(p as IList));

        _ = LoadSavedPlaceIconsAsync();   // warm up saved-place icons in the background
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

    // #29 Inject: run a JS snippet in each selected account's authenticated CloakBrowser session.
    private async Task InjectScriptAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select at least one account."); return; }

        var js = DialogService.Prompt("Inject script",
            "JavaScript to run in each account's logged-in browser (e.g. a bookmarklet one-liner):");
        if (string.IsNullOrWhiteSpace(js)) return;

        Busy = true;
        int ok = 0, fail = 0;
        bool chromiumOffered = false;
        foreach (var acc in sel)
        {
            _main.SetStatus($"Injecting into {acc.DisplayNameOrUser}…");
            var r = await BrowserService.OpenLoggedInAsync(acc, js);

            if (!r.Success && r.Message == "no-chromium")
            {
                if (chromiumOffered) { fail++; continue; }   // only ask once per batch
                chromiumOffered = true;
                Busy = false;
                if (DialogService.Confirm("Chromium required",
                    "Injecting a script uses a private, portable Chromium (downloaded once, ~330 MB, kept separate from your normal browser). Download it now?")
                    && DialogService.ShowChromiumDownload())
                {
                    Busy = true;
                    r = await BrowserService.OpenLoggedInAsync(acc, js);
                }
                else { _main.SetStatus("Injection cancelled — Chromium is required."); return; }
            }

            if (r.Success) ok++; else fail++;
        }
        Busy = false;
        _main.SetStatus(fail == 0
            ? $"Injected the script into {ok} account(s)."
            : $"Injected into {ok} account(s); {fail} failed.");
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

    private List<Account> Sel(IList? list)
    {
        // Checkboxes win: if any accounts are ticked, act on exactly those.
        var ticked = _store.Accounts.Where(a => a.IsChecked).ToList();
        if (ticked.Count > 0) return ticked;
        if (list != null)
        {
            var picked = list.Cast<Account>().ToList();
            if (picked.Count > 0) return picked;
        }
        return _selected != null ? new List<Account> { _selected } : new();
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

    /// <summary>
    /// Imports from ic3w0lf's "Roblox Account Manager". Auto-detects its data
    /// file where possible; otherwise the user browses to it. The file is read
    /// tolerantly and every visible cookie is validated with Roblox before it's
    /// added. Encrypted (master-password) stores yield no cookies — we say so and
    /// point the user at ic3w0lf's Export feature rather than guessing.
    /// </summary>
    private async Task ImportIc3w0lfAsync()
    {
        var auto = Ic3w0lfImportService.AutoLocate();
        var path = DialogService.PickFile(
            "Import from ic3w0lf's Roblox Account Manager",
            "ic3w0lf account data|AccountData;AccountData.json;accounts.json|All files|*.*",
            auto);
        if (string.IsNullOrWhiteSpace(path)) return;

        var read = Ic3w0lfImportService.ReadFile(path);
        if (!read.Ok)
        {
            DialogService.Info("Nothing imported", read.Message);
            return;
        }

        _main.SetStatus(read.Message);
        Busy = true;
        var (added, failed) = await _store.ImportManyAsync(read.Text, s => _main.SetStatus(s));
        Busy = false;
        _main.SetStatus($"ic3w0lf import complete — {added} added, {failed} failed.");
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

    // ==== #29 Power-Tools: server-selection launches ====
    private string? _lastHopJobId;   // remembered so the next hop lands somewhere new

    // Ping-Join: launch the selection into the lowest-ping public server for the place.
    private async Task PingJoinAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select at least one account."); return; }
        if (!TryPlaceId(out long placeId)) return;
        _main.SetStatus("Finding the lowest-ping server…");
        var pick = await PowerToolsService.PickBestPingAsync(placeId);
        if (pick.JobId == null) { _main.SetStatus(pick.Error ?? "No joinable server found."); return; }
        _main.SetStatus(pick.Server!.Ping > 0
            ? $"Best server: {pick.Server.Ping} ms · {pick.Server.Playing}/{pick.Server.MaxPlayers} players — launching…"
            : $"Ping unavailable — joining emptiest server ({pick.Server.Playing}/{pick.Server.MaxPlayers})…");
        _lastHopJobId = pick.JobId;
        await LaunchSequential(sel, placeId, pick.JobId, 0);
    }

    // Server-Hop: launch the selection into a fresh random server, different from the last hop.
    private async Task ServerHopAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select at least one account."); return; }
        if (!TryPlaceId(out long placeId)) return;
        _main.SetStatus("Hopping to a fresh server…");
        var pick = await PowerToolsService.PickHopAsync(placeId, _lastHopJobId);
        if (pick.JobId == null) { _main.SetStatus(pick.Error ?? "No joinable server found."); return; }
        _lastHopJobId = pick.JobId;
        _main.SetStatus($"Hopping into a {pick.Server!.Playing}/{pick.Server.MaxPlayers} server…");
        await LaunchSequential(sel, placeId, pick.JobId, 0);
    }

    // Squad: land the whole checked/selected group in one server that has room for everyone.
    private async Task SquadJoinAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Check or select the squad first."); return; }
        if (!TryPlaceId(out long placeId)) return;
        _main.SetStatus($"Finding a server with room for {sel.Count}…");
        var pick = await PowerToolsService.PickSquadAsync(placeId, sel.Count);
        if (pick.JobId == null) { _main.SetStatus(pick.Error ?? "No joinable server found."); return; }
        if (pick.Warning != null) _main.SetStatus(pick.Warning);
        else _main.SetStatus($"Squad server: {pick.Server!.Playing}/{pick.Server.MaxPlayers} — launching {sel.Count} together…");
        _lastHopJobId = pick.JobId;
        await LaunchSequential(sel, placeId, pick.JobId, 0);
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
        // Launch auto-joins whatever is in the Job-ID / link box: a Job ID, a private-server link, or nothing.
        var t = await ResolveJoinTargetAsync(placeId, sel);
        if (!t.ok) return;
        await LaunchSequential(sel, t.placeId, t.jobId, 0, t.linkCode);
    }

    private async Task JoinServerAsync(IList? list)
    {
        var sel = Sel(list);
        if (sel.Count == 0) { _main.SetStatus("Select at least one account."); return; }
        if (!TryPlaceId(out long placeId)) return;
        if (string.IsNullOrWhiteSpace(JobIdText)) { _main.SetStatus("Enter a Job ID or private-server link, or use Smart Join."); return; }
        var t = await ResolveJoinTargetAsync(placeId, sel);
        if (!t.ok) return;
        if (t.jobId == null && t.linkCode == null) { _main.SetStatus("That doesn't look like a Job ID or private-server link."); return; }
        await LaunchSequential(sel, t.placeId, t.jobId, 0, t.linkCode);
    }

    // Turns the Job-ID / link box into concrete launch parameters.
    private async Task<(bool ok, long placeId, string? jobId, string? linkCode)> ResolveJoinTargetAsync(long placeId, List<Account> sel)
    {
        string input = (JobIdText ?? "").Trim();
        if (input.Length == 0) return (true, placeId, null, null);

        bool looksLikeUrl = input.Contains("roblox.com", StringComparison.OrdinalIgnoreCase)
                            || input.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (looksLikeUrl)
        {
            var parsed = RobloxApi.ParseJoinLink(input);
            long pid = parsed.PlaceId > 0 ? parsed.PlaceId : placeId;

            if (!string.IsNullOrEmpty(parsed.LinkCode))
                return (true, pid, null, parsed.LinkCode);

            if (!string.IsNullOrEmpty(parsed.ShareCode))
            {
                _main.SetStatus("Resolving private-server link…");
                string cookie = sel.FirstOrDefault(a => a.IsValid)?.Cookie
                                ?? _store.Accounts.FirstOrDefault(a => a.IsValid)?.Cookie ?? "";
                var res = await RobloxApi.ResolveShareLinkAsync(cookie, parsed.ShareCode);
                if (res == null) { _main.SetStatus("Couldn't resolve that private-server link — is it still valid?"); return (false, 0, null, null); }
                return (true, res.PlaceId, null, res.LinkCode);
            }

            if (pid > 0) return (true, pid, null, null);   // plain game link -> normal join
            _main.SetStatus("Couldn't read a Place ID from that link — set one above.");
            return (false, 0, null, null);
        }

        // Not a URL: treat as a Job ID (GUID). LauncherService sanitises it further.
        return (true, placeId, input, null);
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

    private async Task LaunchSequential(List<Account> accounts, long placeId, string? job, long followId, string? linkCode = null)
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
            var r = await LauncherService.LaunchAsync(a, placeId, job, followId, linkCode);
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

    private void ToggleFavorite(Account? acc)
    {
        acc ??= _selected;
        if (acc == null) { _main.SetStatus("Select an account first."); return; }
        acc.IsFavorite = !acc.IsFavorite;
        // Remove + re-add so the grouped/sorted CollectionView floats favorites to the top.
        int idx = _store.Accounts.IndexOf(acc);
        if (idx >= 0)
        {
            _store.Accounts.RemoveAt(idx);
            _store.Accounts.Add(acc);
        }
        Selected = acc;
        _store.Save();
        _main.SetStatus(acc.IsFavorite ? $"Pinned {acc.DisplayNameOrUser}." : $"Unpinned {acc.DisplayNameOrUser}.");
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
        SavedPlaces.Add(new SavedPlace
        {
            Name = name,
            PlaceId = placeId,
            IconUrl = GameIcon,
            UniverseId = _lastUniverseId
        });

        PersistSavedPlaces();
        _ = LoadSavedPlaceIconsAsync();   // fill the icon if the live lookup hadn't finished yet
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

    private bool _loadingIcons;
    private async Task LoadSavedPlaceIconsAsync()
    {
        if (_loadingIcons) return;
        var missing = SavedPlaces.Where(p => string.IsNullOrEmpty(p.IconUrl) && p.PlaceId > 0).ToList();
        if (missing.Count == 0) return;
        _loadingIcons = true;
        try
        {
            string cookie = _store.Accounts.FirstOrDefault(a => a.IsValid)?.Cookie ?? "";
            bool changed = false;
            foreach (var place in missing)
            {
                try
                {
                    long universeId = place.UniverseId;
                    if (universeId <= 0)
                    {
                        var info = await RobloxApi.GetPlaceInfoAsync(cookie, place.PlaceId);
                        if (info == null) continue;
                        universeId = info.UniverseId;
                    }
                    string? icon = await RobloxApi.GetGameIconAsync(universeId);
                    if (string.IsNullOrEmpty(icon)) continue;
                    App.Current.Dispatcher.Invoke(() => { place.UniverseId = universeId; place.IconUrl = icon; });
                    changed = true;
                }
                catch { }
            }
            if (changed) App.Current.Dispatcher.Invoke(PersistSavedPlaces);
        }
        finally { _loadingIcons = false; }
    }

    private void PersistSavedPlaces()
    {
        SettingsService.Current.SavedPlaces = SavedPlaces.ToList();
        SettingsService.Save();
    }
}
