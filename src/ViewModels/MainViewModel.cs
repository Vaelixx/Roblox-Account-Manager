using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public static class Pages
{
    public const int Overview = 0;
    public const int Accounts = 1;
    public const int Friends = 2;
    public const int Servers = 3;
    public const int Automation = 4;
    public const int Settings = 5;
}

public class NavItem : ObservableObject
{
    public string TitleKey { get; init; } = "";
    public string Title => L.T(TitleKey);
    public string IconKey { get; init; } = "";
    public int Index { get; init; }
    public string Shortcut { get; init; } = "";

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }

    private string _badge = "";
    /// <summary>Small count shown at the trailing edge (e.g. accounts needing attention).</summary>
    public string Badge { get => _badge; set => SetField(ref _badge, value); }

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

/// <summary>One account group in the sidebar; clicking it filters the Accounts page.</summary>
public class GroupNavItem : ObservableObject
{
    public string Name { get; init; } = "";

    private int _count;
    public int Count { get => _count; set => SetField(ref _count, value); }

    private int _online;
    public int Online { get => _online; set => SetField(ref _online, value); }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }
}

public class MainViewModel : ObservableObject
{
    public AccountStore Store { get; }
    public AccountsViewModel Accounts { get; }
    public ServerBrowserViewModel Servers { get; }
    public SettingsViewModel Settings { get; }
    public DashboardViewModel Dashboard { get; }
    public FriendsViewModel Friends { get; }
    public AutomationViewModel Automation { get; }
    public CommandPaletteViewModel Palette { get; }

    public ObservableCollection<ToastItem> Toasts => ToastService.Items;

    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem { TitleKey = "Nav.Overview",   IconKey = "Icon.Home",       Index = Pages.Overview,   Shortcut = "Ctrl+1" },
        new NavItem { TitleKey = "Nav.Accounts",   IconKey = "Icon.Accounts",   Index = Pages.Accounts,   Shortcut = "Ctrl+2" },
        new NavItem { TitleKey = "Nav.Friends",    IconKey = "Icon.Friends",    Index = Pages.Friends,    Shortcut = "Ctrl+3" },
        new NavItem { TitleKey = "Nav.Servers",    IconKey = "Icon.Servers",    Index = Pages.Servers,    Shortcut = "Ctrl+4" },
        new NavItem { TitleKey = "Nav.Automation", IconKey = "Icon.Automation", Index = Pages.Automation, Shortcut = "Ctrl+5" },
    };

    public NavItem SettingsNav { get; } = new() { TitleKey = "Nav.Settings", IconKey = "Icon.Settings", Index = Pages.Settings, Shortcut = "Ctrl+," };

    /// <summary>Account groups for the sidebar, kept in step with the store.</summary>
    public ObservableCollection<GroupNavItem> Groups { get; } = new();

    private int _selectedIndex;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value > Pages.Settings) return;
            if (!SetField(ref _selectedIndex, value)) return;
            foreach (var n in NavItems) n.IsActive = n.Index == value;
            SettingsNav.IsActive = value == Pages.Settings;
            if (value != Pages.Accounts) SyncGroupHighlight(null);
            else SyncGroupHighlight(Accounts.GroupFilter);

            if (value == Pages.Settings) Settings.OnShown();
            if (value == Pages.Friends) Friends.EnsureLoaded();
            if (value == Pages.Overview) Dashboard.OnShown();
            if (value == Pages.Automation) Automation.OnShown();
            if (value == Pages.Servers)
            {
                if (string.IsNullOrWhiteSpace(Servers.PlaceIdText) && !string.IsNullOrWhiteSpace(Accounts.PlaceIdText))
                    Servers.PlaceIdText = Accounts.PlaceIdText;
                Servers.OnShown();
            }
        }
    }

    private string _status = "";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public void SetStatus(string s)
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) { d.BeginInvoke(new Action(() => Status = s)); return; }
        Status = s;
    }

    public string AppVersionShort => AppInfo.Short;

    private bool _sidebarCollapsed = SettingsService.Current.SidebarCollapsed;
    public bool SidebarCollapsed
    {
        get => _sidebarCollapsed;
        set
        {
            if (!SetField(ref _sidebarCollapsed, value)) return;
            SettingsService.Current.SidebarCollapsed = value;
            SettingsService.Save();
        }
    }

    // ---- running clients (status bar + sidebar) ----
    private int _runningClients;
    public int RunningClients { get => _runningClients; private set { if (SetField(ref _runningClients, value)) OnPropertyChanged(nameof(RunningText)); } }
    public string RunningText => L.N("Status.Running", _runningClients);

    // ---- lock ----
    public bool IsLocked => LockService.IsLocked;
    public bool CanLock => LockService.CanLock;

    private string _unlockError = "";
    public string UnlockError { get => _unlockError; private set => SetField(ref _unlockError, value); }

    public bool IsModalOpen => DialogService.IsModalOpen;

    public RelayCommand NavCommand { get; }
    public RelayCommand SelectGroupCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand LockCommand { get; }
    public RelayCommand UnlockCommand { get; }
    public RelayCommand OpenPaletteCommand { get; }
    public RelayCommand CloseAllClientsCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    // ---- self-update ----
    private static bool s_updateCheckStarted;
    private UpdateInfo? _update;
    private string? _promptedVersion;
    private DispatcherTimer? _updateTimer;
    public bool UpdateAvailable => _update != null;

    private string _updateVersionText = "";
    public string UpdateVersionText { get => _updateVersionText; private set => SetField(ref _updateVersionText, value); }

    public RelayCommand UpdateNowCommand { get; }
    public RelayCommand SkipUpdateCommand { get; }

    private readonly DispatcherTimer _groupRefresh;

    public MainViewModel()
    {
        Store = new AccountStore();
        Accounts = new AccountsViewModel(Store, this);
        Servers = new ServerBrowserViewModel(Store, this);
        Settings = new SettingsViewModel(this);
        Dashboard = new DashboardViewModel(Store, this);
        Friends = new FriendsViewModel(Store, this);
        Automation = new AutomationViewModel(Store, this);
        Palette = new CommandPaletteViewModel(this);

        NavItems[0].IsActive = true;

        NavCommand = new RelayCommand(p =>
        {
            if (p is int i) SelectedIndex = i;
            else if (p != null && int.TryParse(p.ToString(), out var pi)) SelectedIndex = pi;
        });
        SelectGroupCommand = new RelayCommand(p =>
        {
            string? group = p as string;
            Accounts.GroupFilter = string.IsNullOrEmpty(group) ? null : group;
            SelectedIndex = Pages.Accounts;
            SyncGroupHighlight(Accounts.GroupFilter);
        });
        ToggleSidebarCommand = new RelayCommand(_ => SidebarCollapsed = !SidebarCollapsed);
        LockCommand = new RelayCommand(_ =>
        {
            if (!LockService.Lock()) SetStatus(L.T("Lock.NeedPassword"));
        });
        UnlockCommand = new RelayCommand(p => Unlock(p as System.Windows.Controls.PasswordBox));
        OpenPaletteCommand = new RelayCommand(_ => Palette.Open());
        CloseAllClientsCommand = new RelayCommand(_ => CloseAllClients());
        ToggleThemeCommand = new RelayCommand(_ => Settings.ToggleLightDark());

        UpdateNowCommand = new RelayCommand(UpdateNow, () => UpdateAvailable);
        SkipUpdateCommand = new RelayCommand(_ => SkipUpdate(), _ => UpdateAvailable);

        _status = L.T("Status.Ready");

        // Groups: rebuilt shortly after the account list or an account's group changes, so a bulk
        // import does not rebuild the sidebar once per account.
        _groupRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _groupRefresh.Tick += (_, _) => { _groupRefresh.Stop(); RebuildGroups(); };
        Store.Accounts.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (Account a in e.NewItems) a.PropertyChanged += OnAccountPropertyChanged;
            if (e.OldItems != null) foreach (Account a in e.OldItems) a.PropertyChanged -= OnAccountPropertyChanged;
            QueueGroupRefresh();
        };

        LocalizationService.Changed += OnLanguageChanged;
        LockService.Changed += () =>
        {
            UnlockError = "";
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(CanLock));
        };
        DialogService.ModalStateChanged += () => OnPropertyChanged(nameof(IsModalOpen));
        ProcessRegistry.Changed += OnClientsChanged;

        PlaytimeService.Start();
        PlaytimeService.Changed += OnPlaytimeChanged;
        RefreshPlaytime();
        Store.Accounts.CollectionChanged += (_, _) => RefreshPlaytime();

        if (!AppInfo.IsDemo) StartupService.Reconcile(SettingsService.Current.StartWithWindows);
    }

    /// <summary>
    /// Starts the update check, its schedule and the post-update "what's new". Called by the app
    /// once the accounts are open: a prompt over the unlock dialog could otherwise start an update,
    /// which saves the store, before the store was even read.
    /// </summary>
    public void StartAfterUnlock()
    {
        if (AppInfo.IsDemo) return;
        if (!s_updateCheckStarted && SettingsService.Current.CheckUpdatesOnStartup)
        {
            s_updateCheckStarted = true;
            _ = CheckForUpdateAsync();
        }
        ApplyUpdateSchedule();
        _ = ShowWhatsNewIfUpdatedAsync();
    }

    // ================================================================ groups

    private void OnAccountPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Account.Group) or nameof(Account.Presence) or nameof(Account.Health))
            QueueGroupRefresh();
    }

    private void QueueGroupRefresh()
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) { d.BeginInvoke(new Action(QueueGroupRefresh)); return; }
        _groupRefresh.Stop();
        _groupRefresh.Start();
    }

    private void RebuildGroups()
    {
        var accounts = Store.Accounts.ToList();
        var buckets = accounts.GroupBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Update in place where possible so the sidebar does not flicker on every presence poll.
        var names = buckets.Select(b => b.Key).ToList();
        for (int i = Groups.Count - 1; i >= 0; i--)
            if (!names.Contains(Groups[i].Name, StringComparer.OrdinalIgnoreCase)) Groups.RemoveAt(i);

        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            var item = Groups.FirstOrDefault(g => g.Name.Equals(b.Key, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                item = new GroupNavItem { Name = b.Key };
                Groups.Insert(Math.Min(i, Groups.Count), item);
            }
            else if (Groups.IndexOf(item) != i)
            {
                Groups.Move(Groups.IndexOf(item), i);
            }
            item.Count = b.Count();
            item.Online = b.Count(a => a.IsOnline);
        }

        // A filter on a group that no longer exists would leave the list mysteriously empty.
        if (Accounts.GroupFilter != null && !names.Contains(Accounts.GroupFilter, StringComparer.OrdinalIgnoreCase))
            Accounts.GroupFilter = null;
        SyncGroupHighlight(SelectedIndex == Pages.Accounts ? Accounts.GroupFilter : null);

        int attention = accounts.Count(a => a.NeedsAttention);
        NavItems[Pages.Overview].Badge = attention > 0 ? attention.ToString() : "";
        NavItems[Pages.Accounts].Badge = accounts.Count > 0 ? accounts.Count.ToString() : "";
        OnPropertyChanged(nameof(HasGroups));
    }

    /// <summary>The group list is only worth showing once there is more than one group.</summary>
    public bool HasGroups => Groups.Count > 1;

    public void SyncGroupHighlight(string? group)
    {
        foreach (var g in Groups)
            g.IsActive = group != null && g.Name.Equals(group, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ clients

    private void OnClientsChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(new Action(() =>
        {
            var all = ProcessRegistry.All;
            RunningClients = all.Count;
            foreach (var a in Store.Accounts)
                a.RunningClients = all.Count(t => t.UserId == a.UserId && !t.IsExternal);
            Dashboard.RefreshClients();
        }));
    }

    public void CloseAllClients()
    {
        _ = Task.Run(() =>
        {
            int n = InstanceControlService.CloseAll();
            SetStatus(n > 0 ? L.N("Status.ClosedClients", n) : L.T("Status.NoClients"));
        });
    }

    // ================================================================ lock

    /// <summary>Re-reads whether locking is possible (after the master password was set or removed).</summary>
    public void RaiseLockState()
    {
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(IsLocked));
    }

    private void Unlock(System.Windows.Controls.PasswordBox? box)
    {
        if (box == null) return;
        int wait = LockService.RetryDelaySeconds;
        if (wait > 0) { UnlockError = L.N("Lock.Wait", wait); return; }

        if (LockService.TryUnlock(box.Password))
        {
            box.Clear();
            UnlockError = "";
        }
        else
        {
            box.Clear();
            wait = LockService.RetryDelaySeconds;
            UnlockError = wait > 0 ? L.N("Lock.Wait", wait) : L.T("Lock.Wrong");
        }
    }

    // ================================================================ language

    private void OnLanguageChanged()
    {
        foreach (var n in NavItems) n.RefreshTitle();
        SettingsNav.RefreshTitle();
        foreach (var a in Store.Accounts) a.RaiseLocalized();
        OnPropertyChanged(string.Empty);
        Accounts.RefreshLocalized();
        Dashboard.RefreshLocalized();
        Friends.RefreshLocalized();
        Servers.RefreshLocalized();
        Automation.RefreshLocalized();
        Settings.RefreshLocalized();
        RefreshPlaytime();
    }

    // ================================================================ playtime

    private void OnPlaytimeChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.BeginInvoke(new Action(RefreshPlaytime));
        else RefreshPlaytime();
    }

    public void RefreshPlaytime()
    {
        PlaytimeService.Apply(Store.Accounts);
        Dashboard.RefreshPlaytime();
        Settings.RefreshPlaytimeStatus();
    }

    // ================================================================ updates

    public void ApplyUpdateSchedule()
    {
        _updateTimer?.Stop();
        _updateTimer = null;

        var s = SettingsService.Current;
        if (!s.AutoCheckUpdates) return;

        // GitHub answers an anonymous client 60 times an hour; releases don't come minute by minute.
        int minutes = Math.Max(15, s.UpdateCheckMinutes);
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        _updateTimer.Start();
    }

    private string _updateCheckStatus = "";
    public string UpdateCheckStatus
    {
        get => _updateCheckStatus.Length > 0 ? _updateCheckStatus : L.T("Updates.NotChecked");
        private set => SetField(ref _updateCheckStatus, value);
    }

    private bool _updateCheckRunning;
    public bool UpdateCheckRunning { get => _updateCheckRunning; private set => SetField(ref _updateCheckRunning, value); }

    /// <summary>Manual check: always reports an outcome, including "up to date" and "failed".</summary>
    public async Task CheckForUpdateNowAsync()
    {
        if (UpdateCheckRunning) return;
        UpdateCheckRunning = true;
        UpdateCheckStatus = L.T("Updates.Checking");
        SetStatus(L.T("Updates.Checking"));
        try
        {
            var info = await UpdateService.CheckForUpdateAsync(ignoreSkip: true);
            string stamp = DateTime.Now.ToString("t");

            if (info == null && UpdateService.IsRateLimited)
            {
                string until = UpdateService.RateLimitResetsAt?.ToString("t") ?? "";
                UpdateCheckStatus = L.T("Updates.RateLimited", until);
                SetStatus(L.T("Updates.RateLimitedShort"));
                return;
            }

            if (info == null)
            {
                UpdateCheckStatus = L.T("Updates.UpToDate", AppInfo.Short, stamp);
                SetStatus(L.T("Updates.UpToDateShort", AppInfo.Short));
                return;
            }

            if (SettingsService.Current.SkippedUpdateVersion == info.VersionText)
            {
                SettingsService.Current.SkippedUpdateVersion = "";
                SettingsService.Save();
            }

            Adopt(info);
            UpdateCheckStatus = L.T("Updates.AvailableAt", info.VersionText, stamp);
            SetStatus(L.T("Updates.AvailableShort", info.VersionText));
            _promptedVersion = info.VersionText;
            PromptForUpdate(info);
        }
        catch (Exception ex)
        {
            UpdateCheckStatus = L.T("Updates.Failed", ex.Message);
            SetStatus(L.T("Updates.FailedShort"));
        }
        finally { UpdateCheckRunning = false; }
    }

    private void Adopt(UpdateInfo info)
    {
        _update = info;
        UpdateVersionText = info.VersionText;
        OnPropertyChanged(nameof(UpdateAvailable));
        UpdateNowCommand.RaiseCanExecuteChanged();
        SkipUpdateCommand.RaiseCanExecuteChanged();
    }

    private void SkipUpdate()
    {
        if (_update is not { } info) return;

        SettingsService.Current.SkippedUpdateVersion = info.VersionText;
        SettingsService.Save();

        _update = null;
        _promptedVersion = info.VersionText;
        UpdateVersionText = "";
        OnPropertyChanged(nameof(UpdateAvailable));
        UpdateNowCommand.RaiseCanExecuteChanged();
        SkipUpdateCommand.RaiseCanExecuteChanged();
        UpdateCheckStatus = L.T("Updates.Skipped", info.VersionText);
        SetStatus(L.T("Updates.Skipped", info.VersionText));
        Settings.RefreshUpdates();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var info = await UpdateService.CheckForUpdateAsync().ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (info == null)
            {
                await dispatcher.InvokeAsync(() =>
                    UpdateCheckStatus = L.T("Updates.UpToDate", AppInfo.Short, DateTime.Now.ToString("t")));
                return;
            }

            await dispatcher.InvokeAsync(() =>
            {
                Adopt(info);
                UpdateCheckStatus = L.T("Updates.AvailableAt", info.VersionText, DateTime.Now.ToString("t"));

                // Prompt once per discovered version; later polls keep the pill but never nag.
                if (_promptedVersion != info.VersionText && !LockService.IsLocked)
                {
                    _promptedVersion = info.VersionText;
                    PromptForUpdate(info);
                }
            });
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("update", "Background update check failed", ex);
        }
    }

    public async Task ShowWhatsNewAsync()
    {
        try
        {
            string current = UpdateService.CurrentVersionText;
            var notes = await UpdateService.GetNotesForCurrentVersionAsync();
            DialogService.ShowModal(new Views.WhatsNewWindow(current, notes?.Notes, notes?.PageUrl));
        }
        catch (Exception ex)
        {
            SetStatus(L.T("Updates.ChangelogFailed", ex.Message));
        }
    }

    private void UpdateNow()
    {
        if (_update is { } info) PromptForUpdate(info);
    }

    private void PromptForUpdate(UpdateInfo info)
    {
        var win = new Views.UpdatePromptWindow(info);
        if (DialogService.ShowModal(win) != true)
        {
            if (win.Skipped) SkipUpdate();
            return;
        }

        if (UpdateService.BeginUpdate(info))
        {
            // The prompt already showed these notes; don't repeat them after the restart.
            SettingsService.Current.UpdateNotesSeenFor = info.VersionText;
            SettingsService.Save();
            Store.Save();
            Application.Current?.Shutdown();
        }
        else
        {
            SetStatus(L.T("Updates.StartFailed"));
        }
    }

    /// <summary>
    /// Shows the changelog once after an update — when the running version differs from the last one
    /// recorded. A fresh install records the version silently; there is nothing new to announce.
    /// </summary>
    private async Task ShowWhatsNewIfUpdatedAsync()
    {
        try
        {
            var s = SettingsService.Current;
            string current = UpdateService.CurrentVersionText;

            bool seenInPrompt = s.UpdateNotesSeenFor == current;
            bool flagWasSet = s.UpdateNotesSeenFor.Length > 0;
            if (flagWasSet) s.UpdateNotesSeenFor = "";

            if (s.LastSeenVersion == current)
            {
                if (flagWasSet) SettingsService.Save();
                return;
            }

            bool firstInstall = string.IsNullOrEmpty(s.LastSeenVersion);
            s.LastSeenVersion = current;
            SettingsService.Save();
            if (firstInstall || seenInPrompt) return;

            var notes = await UpdateService.GetNotesForCurrentVersionAsync().ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            // Give the main window time to appear so the dialog has an owner to centre on.
            await Task.Delay(800).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
                DialogService.ShowModal(new Views.WhatsNewWindow(current, notes?.Notes, notes?.PageUrl, postUpdate: true)));
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("update", "What's new could not be shown", ex);
        }
    }

    public void OpenServersFor(long placeId)
    {
        Servers.PlaceIdText = placeId.ToString();
        SelectedIndex = Pages.Servers;
        _ = Servers.RefreshAsync();
    }

    /// <summary>Selects an account on the Accounts page (command palette, Overview links).</summary>
    public void ShowAccount(Account account)
    {
        Accounts.GroupFilter = null;
        Accounts.StatusFilter = "All";
        Accounts.SearchText = "";
        SelectedIndex = Pages.Accounts;
        Accounts.Selected = account;
    }
}
