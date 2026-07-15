using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Views;

public partial class AccountsPage : UserControl
{
    private readonly DispatcherTimer _searchDebounce;

    public AccountsPage()
    {
        InitializeComponent();
        // Debounce so typing stays smooth — the (potentially heavy) list filter runs once the
        // user pauses, not on every keystroke.
        _searchDebounce = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(140) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void ApplyFilter()
    {
        var view = ((CollectionViewSource)Resources["GroupedAccounts"]).View;
        if (view == null) return;

        string q = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            if (view.Filter != null) { view.Filter = null; view.Refresh(); }
            return;
        }

        view.Filter = o =>
        {
            if (o is not Account a) return false;
            return a.Username.Contains(q, System.StringComparison.OrdinalIgnoreCase)
                || a.DisplayName.Contains(q, System.StringComparison.OrdinalIgnoreCase)
                || a.Alias.Contains(q, System.StringComparison.OrdinalIgnoreCase)
                || a.Group.Contains(q, System.StringComparison.OrdinalIgnoreCase);
        };
        view.Refresh();
    }
}
