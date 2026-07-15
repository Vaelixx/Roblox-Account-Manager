using System.Collections.ObjectModel;
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

    public RelayCommand NavCommand { get; }

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
    }

    public void SetStatus(string s) => Status = s;

    public void OpenServersFor(long placeId)
    {
        Servers.PlaceIdText = placeId.ToString();
        SelectedIndex = 1;
        _ = Servers.RefreshAsync();
    }
}
