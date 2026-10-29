using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using RobloxAccountManager.Models;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Views;

public partial class AccountsPage : UserControl
{
    public AccountsPage()
    {
        InitializeComponent();
    }

    private AccountsViewModel? Vm => DataContext as AccountsViewModel;

    // ---- keyboard ----------------------------------------------------------------
    // Ctrl+F search · F5 refresh · Enter launch · Space tick · Delete remove · Ctrl+A tick all visible · Esc clear
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool typing = Keyboard.FocusedElement is TextBoxBase or PasswordBox;

        if (e.Key == Key.F && ctrl)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            if (vm.RefreshAllCommand.CanExecute(null)) vm.RefreshAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (typing)
        {
            return;
        }
        else if (e.Key == Key.Enter && vm.Selected != null)
        {
            if (vm.LaunchCommand.CanExecute(null)) vm.LaunchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && vm.Selected != null)
        {
            vm.Selected.IsChecked = !vm.Selected.IsChecked;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && (vm.Selected != null || vm.HasChecked))
        {
            vm.RemoveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.A && ctrl)
        {
            vm.CheckAllVisibleCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.HasChecked)
        {
            vm.ClearChecksCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Escape)
        {
            if (vm.SearchText.Length > 0) vm.SearchText = "";
            else AccountsList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            // Arrow down from the search box moves straight into the results.
            vm.RefreshView();
            if (AccountsList.Items.Count > 0)
            {
                AccountsList.SelectedIndex = Math.Max(0, AccountsList.SelectedIndex);
                (AccountsList.ItemContainerGenerator.ContainerFromIndex(AccountsList.SelectedIndex) as ListBoxItem)?.Focus();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            vm.RefreshView();
            if (vm.Selected == null && AccountsList.Items.Count > 0) AccountsList.SelectedIndex = 0;
            if (vm.Selected != null && vm.LaunchCommand.CanExecute(null)) vm.LaunchCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- list --------------------------------------------------------------------

    /// <summary>Ctrl+click ticks a row instead of moving the selection (multi-account actions).</summary>
    private void AccountsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is Account acc)
        {
            acc.IsChecked = !acc.IsChecked;
            e.Handled = true;
        }
    }

    /// <summary>Double-click a row to launch it.</summary>
    private void AccountsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        var origin = e.OriginalSource as DependencyObject;
        if (FindAncestor<ListBoxItem>(origin) is not { DataContext: Account acc } || FindAncestor<ButtonBase>(origin) != null) return;
        // The row that was double-clicked, not the ticked accounts (unless it is one of them).
        vm.RowTarget = acc;
        try { if (vm.LaunchCommand.CanExecute(null)) vm.LaunchCommand.Execute(null); }
        finally { vm.RowTarget = null; }
    }

    private void AccountsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Right-click selects the row under the pointer, so the menu always acts on what was clicked:
        // that row alone when it isn't ticked, all ticked accounts when it is.
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            if (Vm is { } vm) vm.RowTarget = item.DataContext as Account;
        }
    }

    private void AccountsList_ContextMenuClosing(object sender, ContextMenuEventArgs e)
    {
        // A menu item's command runs after the menu has closed (at render priority): clear the row after it.
        if (Vm is { } vm) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => vm.RowTarget = null);
    }

    // ---- menus -------------------------------------------------------------------

    private void MoreMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private void ImportMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private static void OpenMenu(object sender)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.PlacementTarget = element;
            menu.Placement = PlacementMode.Bottom;
            menu.VerticalOffset = 4;
            menu.IsOpen = true;
        }
    }

    // ---- inspector ----------------------------------------------------------------

    private void FollowBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.FollowCommand.CanExecute(null) == true)
        {
            Vm.FollowCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Detail fields commit on focus loss; persist right away so nothing typed is lost.</summary>
    private void Detail_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitBinding(sender);
        Vm?.CommitDetails();
    }

    private void Detail_Changed(object sender, RoutedEventArgs e) => Vm?.CommitDetails();

    private void TotpSecret_LostFocus(object sender, RoutedEventArgs e)
    {
        // Push the edit into the account first — the binding's own LostFocus handler may run after
        // this one, and the code would then be generated from the previous secret.
        CommitBinding(sender);
        Vm?.OnTotpSecretEdited();
    }

    private static void CommitBinding(object sender)
    {
        if (sender is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
