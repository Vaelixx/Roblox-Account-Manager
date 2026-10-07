using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public class AccountsViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _filterDebounce;

    public AccountStore Store => _store;

    /// <summary>The list as shown: filtered, sorted and (optionally) grouped.</summary>
    public ListCollectionView AccountsView { get; }

    public static readonly string[] StatusFilters = { "All", "Online", "InGame", "Offline", "Attention", "Favorites" };
    public static readonly string[] SortModes = { "Name", "Status", "Recent", "Robux", "Playtime" };

    public AccountsViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;

        AccountsView = new ListCollectionView(_store.Accounts)
        {
            IsLiveFiltering = true,
            IsLiveSorting = true,
            IsLiveGrouping = true,
        };
        foreach (var p in new[] { nameof(Account.Presence), nameof(Account.NeedsAttention), nameof(Account.IsFavorite), nameof(Account.Group) })
            AccountsView.LiveFilteringProperties.Add(p);
        foreach (var p in new[] { nameof(Account.Presence), nameof(Account.IsFavorite), nameof(Account.Group), nameof(Account.DisplayNameOrUser), nameof(Account.Robux) })
            AccountsView.LiveSortingProperties.Add(p);
        AccountsView.LiveGroupingProperties.Add(nameof(Account.Group));
        AccountsView.Filter = FilterAccount;

        _sortMode = SortModes.Contains(SettingsService.Current.AccountSort) ? SettingsService.Current.AccountSort : "Name";
        _groupAccounts = SettingsService.Current.GroupAccounts;
        ApplySortAndGrouping();

        _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _filterDebounce.Tick += (_, _) => { _filterDebounce.Stop(); RefreshView(); };

        // Custom sorting is not re-applied live, so an order that depends on presence or on the set of
        // groups is re-applied shortly after those change (batched: a presence poll touches every row).
        _reshape = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _reshape.Tick += (_, _) => { _reshape.Stop(); ApplySortAndGrouping(); };

        _store.Accounts.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (Account a in e.NewItems) a.PropertyChanged += OnAccountChanged;
            if (e.OldItems != null) foreach (Account a in e.OldItems) a.PropertyChanged -= OnAccountChanged;
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                foreach (var a in _store.Accounts) { a.PropertyChanged -= OnAccountChanged; a.PropertyChanged += OnAccountChanged; }
            if (_selected != null && !_store.Accounts.Contains(_selected)) Selected = null;
            RaiseCounts();
            QueueReshape();
        };

        PlaceIdText = SettingsService.Current.DefaultPlaceId > 0 ? SettingsService.Current.DefaultPlaceId.ToString() : "";
        // The server box and the player box keep what was last launched with, like the place does.
        JobIdText = SettingsService.Current.LastServerInput;
        FollowText = SettingsService.Current.LastFollowInput;
        foreach (var p in SettingsService.Current.SavedPlaces) SavedPlaces.Add(p);

        AddCommand = new AsyncRelayCommand(_ => AddAsync(0));
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ImportIc3w0lfCommand = new AsyncRelayCommand(ImportIc3w0lfAsync);
        ExportCommand = new RelayCommand(_ => Export());
        FindDuplicatesCommand = new RelayCommand(_ => FindDuplicates());
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);

        LaunchCommand = new AsyncRelayCommand(_ => LaunchAsync());
        StopLaunchCommand = new RelayCommand(_ => { try { _launchCts?.Cancel(); } catch (ObjectDisposedException) { } });
        ShuffleJoinCommand = new AsyncRelayCommand(_ => ShuffleJoinAsync());
        PingJoinCommand = new AsyncRelayCommand(_ => PingJoinAsync());
        ServerHopCommand = new AsyncRelayCommand(_ => ServerHopAsync());
        SquadJoinCommand = new AsyncRelayCommand(_ => SquadJoinAsync());
        FollowCommand = new AsyncRelayCommand(_ => FollowAsync());
        BrowseServersCommand = new RelayCommand(_ => BrowseServers());

        RemoveCommand = new RelayCommand(_ => Remove());
        CopyCookieCommand = new RelayCommand(_ => CopyCookie());
        CopyUserIdCommand = new RelayCommand(_ => CopyUserId());
        ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as Account ?? _selected));
        SetGroupCommand = new RelayCommand(_ => SetGroupForTargets());
        SetColorCommand = new RelayCommand(p => SetColor(p as string));
        OpenBrowserCommand = new AsyncRelayCommand(_ => OpenBrowserAsync());
        OpenProfileCommand = new RelayCommand(_ => { if (_selected != null) BrowserService.OpenProfile(_selected); });
        OpenAppCommand = new AsyncRelayCommand(_ => OpenAppAsync());
        InjectScriptCommand = new AsyncRelayCommand(_ => InjectScriptAsync());
        ValidateSelectedCommand = new AsyncRelayCommand(_ => ValidateSelectedAsync());
        SignInAgainCommand = new AsyncRelayCommand(_ => AddAsync(0));
        TestAccountProxyCommand = new AsyncRelayCommand(_ => TestAccountProxyAsync());

        SavePlaceCommand = new RelayCommand(_ => SavePlace());
        RemovePlaceCommand = new RelayCommand(p => RemovePlace(p as SavedPlace));
        ApplySavedPlaceCommand = new RelayCommand(p => ApplySavedPlace(p as SavedPlace));
        CopyTotpCommand = new RelayCommand(_ => CopyTotp());

        ClearChecksCommand = new RelayCommand(_ => { foreach (var a in _store.Accounts) a.IsChecked = false; });
        CheckAllVisibleCommand = new RelayCommand(_ => { foreach (var a in AccountsView.OfType<Account>()) a.IsChecked = true; });
        ClearFiltersCommand = new RelayCommand(_ => { SearchText = ""; StatusFilter = "All"; GroupFilter = null; });
        SetStatusFilterCommand = new RelayCommand(p => StatusFilter = p as string ?? "All");
        SetTabCommand = new RelayCommand(p => InspectorTab = p as string ?? "Overview");

        _ = LoadSavedPlaceIconsAsync();
    }

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Account.IsChecked):
                RaiseChecked();
                break;
            case nameof(Account.Presence):
                RaiseCounts();
                if (_sortMode == "Status") QueueReshape();
                break;
            case nameof(Account.NeedsAttention):
            case nameof(Account.IsFavorite):
                RaiseCounts();
                QueueReshape();
                break;
            case nameof(Account.Group):
                QueueReshape();
                break;
            case nameof(Account.Robux) when _sortMode == "Robux":
            case nameof(Account.PlaytimeTotal) when _sortMode == "Playtime":
                QueueReshape();
                break;
        }
        if (ReferenceEquals(sender, _selected) && e.PropertyName == nameof(Account.TotpSecret)) RefreshTotp();
    }

    private readonly DispatcherTimer _reshape;

    private void QueueReshape()
    {
        _reshape.Stop();
        _reshape.Start();
    }

    public void RefreshLocalized()
    {
        OnPropertyChanged(string.Empty);
        RefreshView();
    }

    // ================================================================ filtering, sorting, grouping

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value ?? "")) return;
            _filterDebounce.Stop();
            _filterDebounce.Start();
        }
    }

    private string _statusFilter = "All";
    public string StatusFilter
    {
        get => _statusFilter;
        set { if (SetField(ref _statusFilter, StatusFilters.Contains(value) ? value : "All")) RefreshView(); }
    }

    private string? _groupFilter;
    /// <summary>Group picked in the sidebar; null shows every group.</summary>
    public string? GroupFilter
    {
        get => _groupFilter;
        set
        {
            if (!SetField(ref _groupFilter, value)) return;
            ApplySortAndGrouping();
            RefreshView();
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(Title));
        }
    }

    private string _sortMode;
    public string SortMode
    {
        get => _sortMode;
        set
        {
            if (!SortModes.Contains(value) || !SetField(ref _sortMode, value)) return;
            SettingsService.Current.AccountSort = value;
            SettingsService.Save();
            ApplySortAndGrouping();
        }
    }

    private bool _groupAccounts;
    public bool GroupAccounts
    {
        get => _groupAccounts;
        set
        {
            if (!SetField(ref _groupAccounts, value)) return;
            SettingsService.Current.GroupAccounts = value;
            SettingsService.Save();
            ApplySortAndGrouping();
        }
    }

    public bool IsCompact
    {
        get => string.Equals(SettingsService.Current.AccountViewMode, "Compact", StringComparison.OrdinalIgnoreCase);
        set
        {
            SettingsService.Current.AccountViewMode = value ? "Compact" : "Card";
            SettingsService.Save();
            OnPropertyChanged();
        }
    }

    public void RefreshViewMode() => OnPropertyChanged(nameof(IsCompact));

    private void ApplySortAndGrouping()
    {
        using (AccountsView.DeferRefresh())
        {
            AccountsView.GroupDescriptions.Clear();
            AccountsView.SortDescriptions.Clear();
            AccountsView.CustomSort = null;

            bool grouped = _groupAccounts && _groupFilter == null && _store.Groups.Skip(1).Any();
            if (grouped) AccountsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Account.Group)));

            AccountsView.CustomSort = new AccountComparer(_sortMode, grouped);
        }
        OnPropertyChanged(nameof(IsGrouped));
    }

    public bool IsGrouped => AccountsView.GroupDescriptions.Count > 0;

    private sealed class AccountComparer : System.Collections.IComparer
    {
        private readonly string _mode;
        private readonly bool _grouped;
        public AccountComparer(string mode, bool grouped) { _mode = mode; _grouped = grouped; }

        public int Compare(object? x, object? y)
        {
            if (x is not Account a || y is not Account b) return 0;
            int c;
            if (_grouped)
            {
                c = GroupRank(a.Group).CompareTo(GroupRank(b.Group));
                if (c != 0) return c;
                c = string.Compare(a.Group, b.Group, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
            }
            c = b.IsFavorite.CompareTo(a.IsFavorite);
            if (c != 0) return c;

            c = _mode switch
            {
                "Status" => PresenceStatus.Rank(a.Presence).CompareTo(PresenceStatus.Rank(b.Presence)),
                "Recent" => b.LastUse.CompareTo(a.LastUse),
                "Robux" => b.Robux.CompareTo(a.Robux),
                "Playtime" => b.PlaytimeTotal.CompareTo(a.PlaytimeTotal),
                _ => 0,
            };
            if (c != 0) return c;
            return string.Compare(a.DisplayNameOrUser, b.DisplayNameOrUser, StringComparison.CurrentCultureIgnoreCase);
        }

        private static int GroupRank(string g) => g.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private bool FilterAccount(object o)
    {
        if (o is not Account a) return false;

        if (_groupFilter != null && !a.Group.Equals(_groupFilter, StringComparison.OrdinalIgnoreCase)) return false;

        bool statusOk = _statusFilter switch
        {
            "Online" => a.IsOnline,
            "InGame" => a.Presence == PresenceStatus.InGame,
            "Offline" => !a.IsOnline,
            "Attention" => a.NeedsAttention,
            "Favorites" => a.IsFavorite,
            _ => true,
        };
        if (!statusOk) return false;

        string q = _searchText.Trim();
        if (q.Length == 0) return true;
        return a.Username.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || a.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || a.Alias.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || a.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || a.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (a.UserId > 0 && a.UserId.ToString().Contains(q, StringComparison.Ordinal));
    }

    public void RefreshView()
    {
        AccountsView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(HasActiveFilter));
    }

    public int VisibleCount => AccountsView.Count;
    public bool HasActiveFilter => _searchText.Trim().Length > 0 || _statusFilter != "All" || _groupFilter != null;
    public bool IsFilterEmpty => _store.Accounts.Count > 0 && AccountsView.Count == 0;
    public bool IsStoreEmpty => _store.Accounts.Count == 0;

    // ---- chip counts ----
    public int CountAll => _store.Accounts.Count(InGroupScope);
    public int CountOnline => _store.Accounts.Count(a => InGroupScope(a) && a.IsOnline);
    public int CountInGame => _store.Accounts.Count(a => InGroupScope(a) && a.Presence == PresenceStatus.InGame);
    public int CountOffline => _store.Accounts.Count(a => InGroupScope(a) && !a.IsOnline);
    public int CountAttention => _store.Accounts.Count(a => InGroupScope(a) && a.NeedsAttention);

    private bool InGroupScope(Account a) => _groupFilter == null || a.Group.Equals(_groupFilter, StringComparison.OrdinalIgnoreCase);

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountOnline));
        OnPropertyChanged(nameof(CountInGame));
        OnPropertyChanged(nameof(CountOffline));
        OnPropertyChanged(nameof(CountAttention));
        OnPropertyChanged(nameof(IsStoreEmpty));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(Subtitle));
    }

    public string Title => _groupFilter ?? L.T("Accounts.Title");

    public string Subtitle
    {
        get
        {
            int total = CountAll, online = CountOnline;
            return total == 0 ? L.T("Accounts.Subtitle.Empty") : L.N("Accounts.Subtitle", total, online);
        }
    }

    // ================================================================ selection

    private Account? _selected;
    public Account? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            SyncGroupOptions();
            RefreshTotp();
            RaiseFFlagWarnings();
        }
    }

    public bool HasSelection => _selected != null;

    public bool MaskUsernames => SettingsService.Current.HideUsernames;
    public void RefreshMask() => OnPropertyChanged(nameof(MaskUsernames));

    private string _inspectorTab = "Overview";
    public string InspectorTab { get => _inspectorTab; set => SetField(ref _inspectorTab, value); }

    // ---- bulk selection (check boxes) ----
    public int CheckedCount => _store.Accounts.Count(a => a.IsChecked);
    public bool HasChecked => CheckedCount > 0;
    public string CheckedText => L.N("Accounts.Bulk.Selected", CheckedCount);

    private void RaiseChecked()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(CheckedText));
    }

    /// <summary>Checked accounts when any are ticked, otherwise the selected one.</summary>
    private List<Account> Targets()
    {
        var ticked = _store.Accounts.Where(a => a.IsChecked).ToList();
        if (ticked.Count > 0) return ticked;
        return _selected != null ? new List<Account> { _selected } : new List<Account>();
    }

    // ================================================================ commands

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ImportIc3w0lfCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand FindDuplicatesCommand { get; }
    public AsyncRelayCommand RefreshAllCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public AsyncRelayCommand ShuffleJoinCommand { get; }
    public AsyncRelayCommand PingJoinCommand { get; }
    public AsyncRelayCommand ServerHopCommand { get; }
    public AsyncRelayCommand SquadJoinCommand { get; }
    public AsyncRelayCommand FollowCommand { get; }
    public RelayCommand BrowseServersCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand CopyCookieCommand { get; }
    public RelayCommand CopyUserIdCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand SetGroupCommand { get; }
    public RelayCommand SetColorCommand { get; }
    public AsyncRelayCommand OpenBrowserCommand { get; }
    public RelayCommand OpenProfileCommand { get; }
    public AsyncRelayCommand OpenAppCommand { get; }
    public AsyncRelayCommand InjectScriptCommand { get; }
    public AsyncRelayCommand ValidateSelectedCommand { get; }
    public AsyncRelayCommand SignInAgainCommand { get; }
    public AsyncRelayCommand TestAccountProxyCommand { get; }
    public RelayCommand SavePlaceCommand { get; }
    public RelayCommand RemovePlaceCommand { get; }
    public RelayCommand ApplySavedPlaceCommand { get; }
    public RelayCommand CopyTotpCommand { get; }
    public RelayCommand ClearChecksCommand { get; }
    public RelayCommand CheckAllVisibleCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand SetStatusFilterCommand { get; }
    public RelayCommand SetTabCommand { get; }

    private bool _busy;
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }

    // A running batch launch, so it can be stopped between accounts.
    private CancellationTokenSource? _launchCts;
    private bool _isLaunching;
    public bool IsLaunching { get => _isLaunching; private set => SetField(ref _isLaunching, value); }
    public RelayCommand StopLaunchCommand { get; }

    // ================================================================ add / import / export

    private async Task AddAsync(int startTab)
    {
        if (LockService.IsLocked) return;
        var added = DialogService.ShowAddAccount(_store, startTab);
        if (added == null) return;
        GroupFilter = null;
        StatusFilter = "All";
        Selected = added;
        _main.SetStatus(L.T("Status.Added", added.DisplayNameOrUser));
        await _store.RefreshLiveDataAsync(new[] { added });
    }

    private async Task ImportAsync()
    {
        var text = DialogService.ShowImport();
        if (string.IsNullOrWhiteSpace(text)) return;
        Busy = true;
        try
        {
            var (added, failed) = await _store.ImportManyAsync(text, s => _main.SetStatus(s));
            _main.SetStatus(L.T("Import.Done", added, failed));
            ToastService.Info(L.T("Import.DoneTitle"), L.T("Import.Done", added, failed));
            await _store.RefreshLiveDataAsync();
        }
        finally { Busy = false; }
    }

    /// <summary>
    /// Imports from ic3w0lf's Roblox Account Manager. The data file is located automatically where
    /// possible and read tolerantly; every cookie found is validated before it is added.
    /// </summary>
    private async Task ImportIc3w0lfAsync()
    {
        var auto = Ic3w0lfImportService.AutoLocate();
        var path = DialogService.PickFile(L.T("Import.Ic3w0lf.Pick"),
            "AccountData|AccountData;AccountData.json;accounts.json|*.*|*.*", auto);
        if (string.IsNullOrWhiteSpace(path)) return;

        var read = Ic3w0lfImportService.ReadFile(path);
        if (!read.Ok)
        {
            DialogService.Info(L.T("Import.Ic3w0lf.NothingTitle"), read.Message);
            return;
        }

        _main.SetStatus(read.Message);
        Busy = true;
        try
        {
            var (added, failed) = await _store.ImportManyAsync(read.Text, s => _main.SetStatus(s));
            _main.SetStatus(L.T("Import.Done", added, failed));
            await _store.RefreshLiveDataAsync();
        }
        finally { Busy = false; }
    }

    private void Export()
    {
        var ticked = _store.Accounts.Where(a => a.IsChecked).ToList();
        var scope = ticked.Count > 0 ? ticked : AccountsView.OfType<Account>().ToList();
        if (scope.Count == 0) { _main.SetStatus(L.T("Export.Nothing")); return; }
        string? path = DialogService.SaveFile(L.T("Export.Title"),
            "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            $"accounts-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path == null) return;
        try
        {
            bool json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            string content = json ? ScaleService.ToJson(scope, includeCookies: false) : ScaleService.ToCsv(scope);
            System.IO.File.WriteAllText(path, content);
            _main.SetStatus(L.N("Export.Done", scope.Count, System.IO.Path.GetFileName(path)));
        }
        catch (Exception ex) { DialogService.Info(L.T("Export.Failed"), ex.Message); }
    }

    private void FindDuplicates()
    {
        var dupes = ScaleService.FindDuplicates(_store.Accounts);
        foreach (var a in _store.Accounts) a.IsChecked = false;
        int n = 0;
        foreach (var grp in dupes)
            foreach (var a in grp) { a.IsChecked = true; n++; }
        if (n == 0) DialogService.Info(L.T("Duplicates.Title"), L.T("Duplicates.None"));
        else _main.SetStatus(L.T("Duplicates.Found", dupes.Count, n));
    }

    private async Task RefreshAllAsync()
    {
        if (_store.Accounts.Count == 0) { _main.SetStatus(L.T("Status.NothingToRefresh")); return; }
        Busy = true;
        _main.SetStatus(L.T("Status.Refreshing"));
        try
        {
            await _store.RefreshLiveDataAsync();
            _main.SetStatus(L.T("Status.Refreshed"));
        }
        finally { Busy = false; }
    }

    // ================================================================ account actions

    private void Remove()
    {
        var targets = Targets();
        if (targets.Count == 0) { _main.SetStatus(L.T("Status.SelectFirst")); return; }
        string names = targets.Count == 1 ? targets[0].DisplayNameOrUser : L.N("Accounts.Count", targets.Count);
        if (!DialogService.Confirm(L.T("Remove.Title"), L.T("Remove.Body", names), L.T("Remove.Action"), danger: true))
            return;
        foreach (var a in targets) _store.Remove(a);
        _main.SetStatus(L.T("Remove.Done", names));
    }

    private void CopyCookie()
    {
        if (_selected == null) return;
        if (string.IsNullOrEmpty(_selected.Cookie)) { _main.SetStatus(L.T("Launch.NoCookie")); return; }
        if (!DialogService.Confirm(L.T("CopyCookie.Title"), L.T("CopyCookie.Body"), L.T("CopyCookie.Action"))) return;
        bool ok = ClipboardService.CopySecret(_selected.Cookie);
        int clearAfter = SettingsService.Current.ClipboardClearSeconds;
        _main.SetStatus(!ok ? L.T("Status.ClipboardBusy")
            : clearAfter > 0 ? L.N("CopyCookie.Done", clearAfter)
            : L.T("CopyCookie.DoneKept"));
        if (ok) AuditLogService.Log(AuditLogService.Category.Cookie, $"Cookie copied for {_selected.Username} (userId {_selected.UserId})");
    }

    private void CopyUserId()
    {
        if (_selected == null || _selected.UserId <= 0) return;
        _main.SetStatus(ClipboardService.CopyText(_selected.UserId.ToString()) ? L.T("Status.Copied") : L.T("Status.ClipboardBusy"));
    }

    private void ToggleFavorite(Account? acc)
    {
        if (acc == null) return;
        acc.IsFavorite = !acc.IsFavorite;
        _store.Save();
        _main.SetStatus(acc.IsFavorite ? L.T("Status.Pinned", acc.DisplayNameOrUser) : L.T("Status.Unpinned", acc.DisplayNameOrUser));
    }

    private void SetGroupForTargets()
    {
        var targets = Targets();
        if (targets.Count == 0) { _main.SetStatus(L.T("Status.SelectFirst")); return; }
        string? g = DialogService.Prompt(L.T("Group.Move.Title"), L.T("Group.Move.Label"), targets[0].Group);
        if (g == null) return;
        g = string.IsNullOrWhiteSpace(g) ? "Default" : g.Trim();
        foreach (var a in targets) a.Group = g;
        _store.Save();
        SyncGroupOptions();
        ApplySortAndGrouping();
        _main.SetStatus(L.N("Group.Moved", targets.Count, g));
    }

    public static readonly string[] TagColors = { "#F0616A", "#F2994A", "#E8C547", "#34C77B", "#3AA0FF", "#9B7BFF", "#E26DC4" };

    private void SetColor(string? hex)
    {
        var targets = Targets();
        if (targets.Count == 0) { _main.SetStatus(L.T("Status.SelectFirst")); return; }
        foreach (var a in targets) a.Color = hex ?? "";
        _store.Save();
    }

    private async Task OpenBrowserAsync()
    {
        if (_selected == null) return;
        var acc = _selected;
        Busy = true;
        _main.SetStatus(L.T("Browser.Opening", acc.DisplayNameOrUser));
        try
        {
            var r = await BrowserService.OpenLoggedInAsync(acc);
            if (!r.Success && r.NoBrowser && OfferBrowserDownload())
                r = await BrowserService.OpenLoggedInAsync(acc);
            _main.SetStatus(r.Message);
            if (!r.Success) ToastService.Error(L.T("Browser.OpenFailedTitle"), r.Message);
        }
        finally { Busy = false; }
    }

    /// <summary>Offers the private browser when no Edge / Chrome exists. True when it is installed afterwards.</summary>
    private static bool OfferBrowserDownload()
        => DialogService.Confirm(L.T("Browser.Download.Title"), L.T("Browser.Download.Body"), L.T("Common.Download"))
           && DialogService.ShowChromiumDownload();

    private async Task InjectScriptAsync()
    {
        var targets = Targets();
        if (targets.Count == 0) { _main.SetStatus(L.T("Status.SelectFirst")); return; }

        var js = DialogService.PromptMultiline(L.T("Inject.Title"), L.T("Inject.Body"), okText: L.T("Inject.Action"));
        if (string.IsNullOrWhiteSpace(js)) return;

        Busy = true;
        int ok = 0, fail = 0;
        try
        {
            foreach (var acc in targets)
            {
                _main.SetStatus(L.T("Inject.Progress", acc.DisplayNameOrUser));
                var r = await BrowserService.OpenLoggedInAsync(acc, js);
                if (!r.Success && r.NoBrowser)
                {
                    if (!OfferBrowserDownload()) { _main.SetStatus(r.Message); return; }
                    r = await BrowserService.OpenLoggedInAsync(acc, js);
                }
                if (r.Success) ok++; else fail++;
            }
        }
        finally { Busy = false; }
        _main.SetStatus(L.T("Inject.Done", ok, fail));
    }

    private async Task OpenAppAsync()
    {
        if (_selected == null) return;
        var acc = _selected;
        Busy = true;
        _main.SetStatus(L.T("App.Opening", acc.DisplayNameOrUser));
        try
        {
            var r = await LauncherService.OpenRobloxAppAsync(acc);
            _store.Save();
            _main.SetStatus(r.Success ? L.T("App.Opened", acc.DisplayNameOrUser) : r.Message);
            if (!r.Success) ToastService.Error(L.T("Launch.FailedTitle"), r.Message);
        }
        finally { Busy = false; }
    }

    private async Task ValidateSelectedAsync()
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        _main.SetStatus(L.T("Health.Checking"));
        int invalid = await CookieHealthService.ValidateAllAsync(targets);
        _store.Save();
        _main.SetStatus(invalid == 0 ? L.N("Health.AllValid", targets.Count) : L.N("Health.SomeInvalid", invalid, targets.Count));
    }

    private async Task TestAccountProxyAsync()
    {
        if (_selected == null || string.IsNullOrWhiteSpace(_selected.ProxyUrl)) { _main.SetStatus(L.T("Proxy.EnterFirst")); return; }
        _store.Save();
        _main.SetStatus(L.T("Proxy.Testing"));
        var (ok, message) = await RobloxApi.TestProxyAsync(_selected.ProxyUrl, "", "");
        _main.SetStatus(message);
        if (ok) ToastService.Success(L.T("Proxy.WorksTitle"), message);
        else ToastService.Error(L.T("Proxy.FailedTitle"), message);
    }

    public bool AccountProxyValid => _selected == null || string.IsNullOrWhiteSpace(_selected.ProxyUrl)
                                     || RobloxApi.TryBuildProxy(_selected.ProxyUrl, null, null) != null;

    /// <summary>Called by the view when a detail field lost focus, so edits are saved without a Save button.</summary>
    public void CommitDetails()
    {
        _store.Save();
        OnPropertyChanged(nameof(AccountProxyValid));
        RaiseFFlagWarnings();
        SyncGroupOptions();
    }

    // ---- per-account FastFlags ----
    public string AccountFFlagWarning
    {
        get
        {
            if (_selected == null || string.IsNullOrWhiteSpace(_selected.FFlags)) return "";
            if (!FFlagsService.IsValidRaw(_selected.FFlags)) return L.T("FFlags.InvalidJson");
            var ignored = FFlagsService.NotAllowlisted(FFlagsService.ParseRaw(_selected.FFlags).Keys);
            return ignored.Count == 0 ? "" : L.T("FFlags.Ignored", string.Join(", ", ignored.Take(6)) + (ignored.Count > 6 ? "…" : ""));
        }
    }

    private void RaiseFFlagWarnings() => OnPropertyChanged(nameof(AccountFFlagWarning));

    // ================================================================ group picker

    public ObservableCollection<string> GroupOptions { get; } = new();
    public string NewGroupOption => L.T("Group.New");

    private bool _suppressGroupApply;
    private string? _selectedGroupOption;
    public string? SelectedGroupOption
    {
        get => _selectedGroupOption;
        set
        {
            if (!SetField(ref _selectedGroupOption, value)) return;
            if (_suppressGroupApply || value == null || _selected == null) return;
            if (value == NewGroupOption)
            {
                string? g = DialogService.Prompt(L.T("Group.New.Title"), L.T("Group.New.Label"));
                if (string.IsNullOrWhiteSpace(g)) { SyncGroupOptions(); return; }
                value = g.Trim();
            }
            if (_selected.Group == value) { SyncGroupOptions(); return; }
            _selected.Group = value;
            _store.Save();
            SyncGroupOptions();
            ApplySortAndGrouping();
            _main.SetStatus(L.N("Group.Moved", 1, value));
        }
    }

    private void SyncGroupOptions()
    {
        _suppressGroupApply = true;
        try
        {
            GroupOptions.Clear();
            var groups = _store.Groups.ToList();
            if (!groups.Contains("Default", StringComparer.OrdinalIgnoreCase)) GroupOptions.Add("Default");
            foreach (var g in groups) GroupOptions.Add(g);
            GroupOptions.Add(NewGroupOption);
            _selectedGroupOption = _selected?.Group;
            OnPropertyChanged(nameof(SelectedGroupOption));
        }
        finally { _suppressGroupApply = false; }
    }

    // ================================================================ 2FA

    private DispatcherTimer? _totpTimer;

    private string _totpCode = "";
    public string TotpCode { get => _totpCode; private set => SetField(ref _totpCode, value); }

    private int _totpSeconds;
    public int TotpSeconds
    {
        get => _totpSeconds;
        private set { if (SetField(ref _totpSeconds, value)) { OnPropertyChanged(nameof(TotpCountdown)); OnPropertyChanged(nameof(TotpProgress)); } }
    }

    public string TotpCountdown => _totpSeconds > 0 ? L.T("Totp.Countdown", _totpSeconds) : "";
    public double TotpProgress => _totpSeconds / 30.0;
    public bool HasTotpCode => !string.IsNullOrEmpty(_totpCode);

    private bool _totpInvalid;
    public bool TotpInvalid { get => _totpInvalid; private set => SetField(ref _totpInvalid, value); }

    private void RefreshTotp()
    {
        UpdateTotpNow();
        bool wanted = !string.IsNullOrWhiteSpace(_selected?.TotpSecret);
        if (wanted && _totpTimer == null)
        {
            _totpTimer = new DispatcherTimer(DispatcherPriority.Normal, Application.Current.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _totpTimer.Tick += (_, _) => UpdateTotpNow();
            _totpTimer.Start();
        }
        else if (!wanted && _totpTimer != null)
        {
            _totpTimer.Stop();
            _totpTimer = null;
        }
    }

    private void UpdateTotpNow()
    {
        var secret = _selected?.TotpSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            TotpCode = ""; TotpSeconds = 0; TotpInvalid = false;
            OnPropertyChanged(nameof(HasTotpCode));
            return;
        }
        string? code = TotpService.Generate(secret);
        TotpCode = code == null ? "" : $"{code[..3]} {code[3..]}";
        TotpInvalid = code == null;
        TotpSeconds = code == null ? 0 : TotpService.SecondsRemaining();
        OnPropertyChanged(nameof(HasTotpCode));
    }

    public void OnTotpSecretEdited()
    {
        _store.Save();
        RefreshTotp();
    }

    private void CopyTotp()
    {
        if (string.IsNullOrEmpty(_totpCode)) return;
        _main.SetStatus(ClipboardService.CopySecret(_totpCode.Replace(" ", "")) ? L.T("Totp.Copied") : L.T("Status.ClipboardBusy"));
    }

    // ================================================================ launching

    private string _placeIdText = "";
    public string PlaceIdText
    {
        get => _placeIdText;
        set { if (SetField(ref _placeIdText, value ?? "")) LookupPlaceDebounced(); }
    }

    private string _jobIdText = "";
    public string JobIdText { get => _jobIdText; set => SetField(ref _jobIdText, value ?? ""); }

    private string _followText = "";
    public string FollowText { get => _followText; set => SetField(ref _followText, value ?? ""); }

    private string _gameName = "";
    public string GameName { get => _gameName; private set { if (SetField(ref _gameName, value)) OnPropertyChanged(nameof(HasGameInfo)); } }

    private string _gameCreator = "";
    public string GameCreator { get => _gameCreator; private set => SetField(ref _gameCreator, value); }

    private string? _gameIcon;
    public string? GameIcon { get => _gameIcon; private set => SetField(ref _gameIcon, value); }

    public bool HasGameInfo => !string.IsNullOrEmpty(_gameName);

    private long _lastUniverseId;
    private CancellationTokenSource? _placeLookupCts;

    /// <summary>A pasted game link fills the Place ID; digits are extracted from anything else.</summary>
    private static long ParsePlaceId(string text) => JoinLinks.ParsePlaceId(text);

    private void LookupPlaceDebounced()
    {
        var previous = _placeLookupCts;
        _placeLookupCts = null;
        if (previous != null)
        {
            try { previous.Cancel(); } catch { }
            previous.Dispose();
        }

        long placeId = ParsePlaceId(_placeIdText);
        if (placeId <= 0)
        {
            GameName = ""; GameCreator = ""; GameIcon = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _placeLookupCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450, token);
                string cookie = _store.Accounts.ToArray().FirstOrDefault(a => a.IsValid && a.Cookie.Length > 0)?.Cookie ?? "";
                var info = await RobloxApi.GetPlaceInfoAsync(cookie, placeId);
                if (token.IsCancellationRequested || info == null) return;
                string? icon = await RobloxApi.GetGameIconAsync(info.UniverseId);
                if (token.IsCancellationRequested) return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _lastUniverseId = info.UniverseId;
                    GameName = info.Name;
                    GameCreator = string.IsNullOrEmpty(info.Creator) ? "" : L.T("Launch.By", info.Creator);
                    GameIcon = icon;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticsService.Warn("accounts", "Game lookup failed", ex); }
        });
    }

    private bool TryPlaceId(out long placeId)
    {
        placeId = ParsePlaceId(PlaceIdText);
        if (placeId <= 0)
        {
            _main.SetStatus(L.T("Launch.NeedPlace"));
            ToastService.Warning(L.T("Launch.NeedPlaceTitle"), L.T("Launch.NeedPlace"));
            return false;
        }
        if (SettingsService.Current.DefaultPlaceId != placeId)
        {
            SettingsService.Current.DefaultPlaceId = placeId;
            SettingsService.Save();
        }
        return true;
    }

    private List<Account>? LaunchTargets()
    {
        var targets = Targets();
        if (targets.Count == 0)
        {
            _main.SetStatus(L.T("Status.SelectFirst"));
            return null;
        }
        return targets;
    }

    private async Task LaunchAsync()
    {
        var sel = LaunchTargets();
        if (sel == null) return;

        // A pasted link can carry the place itself, so the Place ID box may legitimately be empty.
        var target = await ResolveJoinTargetAsync(sel);
        if (target == null) return;
        await LaunchSequential(sel, target);
    }

    /// <summary>
    /// Turns the Place ID and the Job ID / link boxes into a launch target: a plain game, a specific
    /// server (Job ID), a private server (classic link code or modern share link), or a deep link.
    /// </summary>
    private async Task<JoinTarget?> ResolveJoinTargetAsync(List<Account> sel)
    {
        string input = JobIdText.Trim();
        long placeId = ParsePlaceId(PlaceIdText);
        bool isLink = JoinLinks.LooksLikeLink(input);

        // Without a link the place comes from the box, and a valid one is remembered as the default.
        if (!isLink && !TryPlaceId(out placeId)) return null;
        if (isLink && JoinLinks.Parse(input).ShareCode != null) _main.SetStatus(L.T("Launch.ResolvingLink"));

        var result = await JoinTargetResolver.FromServerInputAsync(input, placeId,
            () => sel.FirstOrDefault(a => a.IsValid)?.Cookie ?? _store.Accounts.FirstOrDefault(a => a.IsValid)?.Cookie ?? "");
        if (result.Target != null)
        {
            RememberInput(input, null);
            return result.Target;
        }

        _main.SetStatus(result.Error ?? "");
        if (result.ErrorTitle != null) ToastService.Warning(result.ErrorTitle, result.Error ?? "");
        return null;
    }

    /// <summary>Keeps the launch bar's server / player box for next time (the place box is remembered in <see cref="TryPlaceId"/>).</summary>
    private static void RememberInput(string? server, string? follow)
    {
        var s = SettingsService.Current;
        bool changed = false;
        if (server != null && s.LastServerInput != server) { s.LastServerInput = server; changed = true; }
        if (follow != null && s.LastFollowInput != follow) { s.LastFollowInput = follow; changed = true; }
        if (changed) SettingsService.Save();
    }

    private async Task ShuffleJoinAsync()
    {
        var sel = LaunchTargets();
        if (sel == null || !TryPlaceId(out long placeId)) return;
        _main.SetStatus(L.T("Launch.FindingServer"));
        var s = SettingsService.Current;
        string job = await RobloxApi.GetRandomJobIdAsync(placeId, s.ShuffleLowestServer, s.ShufflePageCount);
        if (string.IsNullOrEmpty(job)) { _main.SetStatus(L.T("Servers.NoneJoinable")); return; }
        await LaunchSequential(sel, new JoinTarget(placeId, job));
    }

    private string? _lastHopJobId;

    private async Task PingJoinAsync()
    {
        var sel = LaunchTargets();
        if (sel == null || !TryPlaceId(out long placeId)) return;
        _main.SetStatus(L.T("Launch.FindingPing"));
        var pick = await PowerToolsService.PickBestPingAsync(placeId);
        if (pick.JobId == null) { _main.SetStatus(pick.Error ?? L.T("Servers.NoneJoinable")); return; }
        _lastHopJobId = pick.JobId;
        await LaunchSequential(sel, new JoinTarget(placeId, pick.JobId));
    }

    private async Task ServerHopAsync()
    {
        var sel = LaunchTargets();
        if (sel == null || !TryPlaceId(out long placeId)) return;
        _main.SetStatus(L.T("Launch.Hopping"));
        var exclude = _lastHopJobId ?? sel.Select(a => a.GameId).FirstOrDefault(g => !string.IsNullOrEmpty(g));
        var pick = await PowerToolsService.PickHopAsync(placeId, exclude);
        if (pick.JobId == null) { _main.SetStatus(pick.Error ?? L.T("Servers.NoneJoinable")); return; }
        _lastHopJobId = pick.JobId;
        await LaunchSequential(sel, new JoinTarget(placeId, pick.JobId));
    }

    private async Task SquadJoinAsync()
    {
        var sel = LaunchTargets();
        if (sel == null || !TryPlaceId(out long placeId)) return;
        _main.SetStatus(L.T("Launch.FindingSquad", sel.Count));
        var pick = await PowerToolsService.PickSquadAsync(placeId, sel.Count);
        if (pick.JobId == null) { _main.SetStatus(pick.Error ?? L.T("Servers.NoneJoinable")); return; }
        if (pick.Warning != null) ToastService.Warning(L.T("Launch.SquadTitle"), pick.Warning);
        _lastHopJobId = pick.JobId;
        await LaunchSequential(sel, new JoinTarget(placeId, pick.JobId));
    }

    private async Task FollowAsync()
    {
        var sel = LaunchTargets();
        if (sel == null) return;
        string user = FollowText.Trim().TrimStart('@');
        if (string.IsNullOrEmpty(user)) { _main.SetStatus(L.T("Follow.EnterUser")); return; }
        _main.SetStatus(L.T("Follow.LookingUp", user));
        long id = await JoinTargetResolver.ResolveUserIdAsync(user);
        if (id <= 0) { _main.SetStatus(L.T("Follow.NotFound", user)); ToastService.Error(L.T("Launch.FailedTitle"), L.T("Follow.NotFound", user)); return; }
        RememberInput(null, FollowText.Trim());
        await LaunchSequential(sel, new JoinTarget(0, FollowUserId: id));
    }

    private void BrowseServers()
    {
        if (!TryPlaceId(out long placeId)) return;
        _main.OpenServersFor(placeId);
    }

    private async Task LaunchSequential(List<Account> accounts, JoinTarget target)
    {
        if (!RequirementsService.IsRobloxInstalled())
        {
            DialogService.OfferDownload(L.T("Requirements.NoRoblox.Title"), L.T("Requirements.NoRoblox.Body"), "https://www.roblox.com/download");
            return;
        }

        Busy = true;
        IsLaunching = true;
        _launchCts = new CancellationTokenSource();
        Account? current = null;
        LauncherService.BatchResult result;
        try
        {
            result = await LauncherService.LaunchBatchAsync(accounts, target,
                new LauncherService.BatchOptions(SettingsService.Current.AccountJoinDelay),
                onLaunching: (a, i) =>
                {
                    if (current != null) current.IsBusy = false;
                    current = a;
                    a.IsBusy = true;
                    _main.SetStatus(accounts.Count == 1
                        ? L.T("Launch.Launching", a.DisplayNameOrUser)
                        : L.T("Launch.LaunchingOf", a.DisplayNameOrUser, i + 1, accounts.Count));
                },
                onWaiting: left =>
                {
                    if (current != null) current.IsBusy = false;
                    _main.SetStatus(left > 0 ? L.T("Launch.NextIn", left) : L.T("Launch.WaitingForClient", current?.DisplayNameOrUser ?? ""));
                }, _launchCts.Token);
            _store.Save();
        }
        finally
        {
            foreach (var a in accounts) a.IsBusy = false;
            _launchCts.Dispose();
            _launchCts = null;
            IsLaunching = false;
            Busy = false;
        }

        if (result.NotStarted > 0)
            _main.SetStatus(L.T("Launch.Stopped", result.Launched, result.NotStarted));
        else if (result.Errors.Count > 0)
        {
            _main.SetStatus(result.Launched > 0 ? L.T("Launch.DoneMixed", result.Launched, result.Failed) : result.Errors[0]);
            ToastService.Error(L.T("Launch.FailedTitle"), string.Join("\n", result.Errors.Take(3)));
        }
        else
        {
            _main.SetStatus(L.N("Launch.Done", result.Launched));
        }
    }

    // ================================================================ saved places

    public ObservableCollection<SavedPlace> SavedPlaces { get; } = new();

    private bool _savedPlacesOpen;
    public bool SavedPlacesOpen
    {
        get => _savedPlacesOpen;
        set { if (SetField(ref _savedPlacesOpen, value) && value) _ = LoadSavedPlaceIconsAsync(); }
    }

    private void SavePlace()
    {
        long placeId = ParsePlaceId(PlaceIdText);
        if (placeId <= 0) { _main.SetStatus(L.T("Launch.NeedPlace")); return; }

        string? name = DialogService.Prompt(L.T("Places.Save.Title"), L.T("Places.Save.Label"), GameName);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        // Saving under an existing name replaces it but keeps its id, so presets using it follow along.
        var existing = SavedPlaces.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) SavedPlaces.Remove(existing);
        SavedPlaces.Add(new SavedPlace
        {
            Id = existing?.Id is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
            Name = name,
            PlaceId = placeId,
            Server = JobIdText.Trim(),
            IconUrl = GameIcon,
            UniverseId = _lastUniverseId,
        });

        PersistSavedPlaces();
        _ = LoadSavedPlaceIconsAsync();
        _main.SetStatus(L.T("Places.Saved", name));
    }

    private void RemovePlace(SavedPlace? place)
    {
        if (place == null) return;
        SavedPlaces.Remove(place);
        PersistSavedPlaces();
    }

    private void ApplySavedPlace(SavedPlace? place)
    {
        if (place == null) return;
        SavedPlacesOpen = false;
        PlaceIdText = place.PlaceId.ToString();
        JobIdText = place.Server;
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
                long universeId = place.UniverseId;
                if (universeId <= 0)
                {
                    var info = await RobloxApi.GetPlaceInfoAsync(cookie, place.PlaceId);
                    if (info == null) continue;
                    universeId = info.UniverseId;
                }
                string? icon = await RobloxApi.GetGameIconAsync(universeId);
                if (string.IsNullOrEmpty(icon)) continue;
                place.UniverseId = universeId;
                place.IconUrl = icon;
                changed = true;
            }
            if (changed) PersistSavedPlaces();
        }
        catch (Exception ex) { DiagnosticsService.Warn("accounts", "Saved place icons could not be loaded", ex); }
        finally { _loadingIcons = false; }
    }

    private void PersistSavedPlaces()
    {
        SettingsService.Current.SavedPlaces = SavedPlaces.ToList();
        SettingsService.Save();
    }
}
