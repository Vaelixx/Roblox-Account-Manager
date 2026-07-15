using System.Collections.ObjectModel;
using System.Windows;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public class NavItem : ObservableObject
{
    public string Title { get; init; } = "";
    public string IconKey { get; init; } = "";
    public int Index { get; init; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }
}

public class MainViewModel : ObservableObject
{
    public AccountStore Store { get; }
    public AccountsViewModel Accounts { get; }
    public ServerBrowserViewModel Servers { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem { Title = "Accounts", IconKey = "Icon.Accounts", Index = 0 },
        new NavItem { Title = "Servers",  IconKey = "Icon.Servers",  Index = 1 },
        new NavItem { Title = "Settings", IconKey = "Icon.Settings", Index = 2 },
    };

    private int _selectedIndex;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetField(ref _selectedIndex, value))
            {
                OnPropertyChanged(nameof(SelectedNav));
                foreach (var n in NavItems) n.IsActive = n.Index == value;
                if (value == 2) Settings.RefreshChromium();
            }
        }
    }

    public NavItem SelectedNav => NavItems[_selectedIndex];

    private string _status = "Ready";
    public string Status { get => _status; set => SetField(ref _status, value); }

    /// <summary>Version badge in the title bar, e.g. "v1.1.0" — read from the built assembly.</summary>
    public string AppVersionShort => AppInfo.Short;

    public RelayCommand NavCommand { get; }

    // ---- self-update ----
    private static bool s_updateCheckStarted; // at most one check per process run

    private UpdateInfo? _update;
    public bool UpdateAvailable => _update != null;

    private string _updateVersionText = "";
    public string UpdateVersionText
    {
        get => _updateVersionText;
        private set => SetField(ref _updateVersionText, value);
    }

    public RelayCommand UpdateNowCommand { get; }

    public MainViewModel()
    {
        NavCommand = new RelayCommand(p =>
        {
            if (p is int i) SelectedIndex = i;
            else if (p != null && int.TryParse(p.ToString(), out var pi)) SelectedIndex = pi;
        });

        Store = new AccountStore();
        Accounts = new AccountsViewModel(Store, this);
        Servers = new ServerBrowserViewModel(Store, this);
        Settings = new SettingsViewModel(this);

        NavItems[0].IsActive = true;

        UpdateNowCommand = new RelayCommand(UpdateNow, () => UpdateAvailable);

        // Fire-and-forget update check on startup, at most once per run; failures are silent.
        if (!s_updateCheckStarted)
        {
            s_updateCheckStarted = true;
            _ = CheckForUpdateAsync();
        }
    }

    public void SetStatus(string s) => Status = s;

    private async Task CheckForUpdateAsync()
    {
        var info = await UpdateService.CheckForUpdateAsync().ConfigureAwait(false);
        if (info == null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        await dispatcher.InvokeAsync(() =>
        {
            _update = info;
            UpdateVersionText = $"Update available — {info.VersionText}";
            OnPropertyChanged(nameof(UpdateAvailable));
        });
    }

    private void UpdateNow()
    {
        if (_update is not { } info) return;

        if (!DialogService.Confirm("Install update",
                $"{info.VersionText} is available. The app will close, install the update and restart.\n\nUpdate now?"))
            return;

        if (UpdateService.BeginUpdate(info))
            Application.Current.Shutdown();
        else
            SetStatus("Update could not be started — please try again later.");
    }

    public void OpenServersFor(long placeId)
    {
        Servers.PlaceIdText = placeId.ToString();
        SelectedIndex = 1;
        _ = Servers.RefreshAsync();
    }
}
