using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.ViewModels;

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

    // ---- Keyboard shortcuts -----------------------------------------------------
    // F5     : refresh every account's presence/thumbnail (safe to fire while typing).
    // Ctrl+F : jump into the search box and select its text.
    // Both are handled here rather than as InputBindings so we can reach the code-behind
    // SearchBox and the DataContext's command in one place.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            if (DataContext is AccountsViewModel vm && vm.RefreshAllCommand.CanExecute(null))
                vm.RefreshAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            // Esc clears an active search first (before any window-level handler sees it).
            ClearSearch();
            e.Handled = true;
        }
    }

    // Enter inside the search box launches the currently matched selection — or, if nothing
    // is selected, the first visible account. Lets you type-then-play without the mouse.
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Flush any pending debounce so the filter reflects what was just typed.
        _searchDebounce.Stop();
        ApplyFilter();

        if (AccountsList.SelectedItem == null && AccountsList.Items.Count > 0)
            AccountsList.SelectedItem = AccountsList.Items[0];
        LaunchSelected();
        e.Handled = true;
    }

    // Double-click a row to launch it — the most direct "play this account" gesture.
    private void AccountsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks that don't land on an actual row (e.g. group headers, empty space).
        if (ItemUnder(e.OriginalSource as DependencyObject) == null) return;
        LaunchSelected();
    }

    private void LaunchSelected()
    {
        if (DataContext is not AccountsViewModel vm) return;
        var sel = AccountsList.SelectedItems;
        if (sel.Count == 0) return;
        if (vm.LaunchCommand.CanExecute(sel))
            vm.LaunchCommand.Execute(sel);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => ClearSearch();

    private void ClearSearch()
    {
        SearchBox.Clear();
        _searchDebounce.Stop();
        ApplyFilter();          // reflect the cleared box immediately
        SearchBox.Focus();
    }

    private void ApplyFilter()
    {
        var view = ((CollectionViewSource)Resources["GroupedAccounts"]).View;
        if (view == null) return;

        string q = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            if (view.Filter != null) { view.Filter = null; view.Refresh(); }
            ResultCount.Visibility = Visibility.Collapsed;
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

        // Show how many accounts survived the filter (Cast avoids materialising a List).
        int matches = System.Linq.Enumerable.Count(System.Linq.Enumerable.OfType<Account>(view));
        ResultCount.Text = matches == 1 ? "1 match" : $"{matches} matches";
        ResultCount.Visibility = Visibility.Visible;
    }

    // ---- Drag & drop reordering -------------------------------------------------
    // The visible order (and grouping) is driven by the source Store.Accounts collection,
    // so a reorder is just a Move() there followed by Save(). Dropping onto an item that
    // lives in another group also reassigns the dragged account's Group to the target's, so
    // cross-group drags feel natural instead of appearing to do nothing.
    private System.Windows.Point _dragStart;
    private Account? _dragItem;

    private void AccountsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemUnder(e.OriginalSource as DependencyObject);
    }

    private void AccountsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;

        var pos = e.GetPosition(null);
        if (System.Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Never hijack a gesture that started on an interactive control (checkbox, button, text).
        var origin = e.OriginalSource as DependencyObject;
        if (FindAncestor<ButtonBase>(origin) != null || FindAncestor<TextBoxBase>(origin) != null)
        {
            _dragItem = null;
            return;
        }

        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(AccountsList, item, DragDropEffects.Move);
    }

    private void AccountsList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Account)) is not Account dragged) return;
        var target = ItemUnder(e.OriginalSource as DependencyObject);
        if (target == null || ReferenceEquals(dragged, target)) return;
        if (DataContext is not AccountsViewModel vm) return;

        var list = vm.Store.Accounts;
        int from = list.IndexOf(dragged);
        int to = list.IndexOf(target);
        if (from < 0 || to < 0) return;

        // Dropping across group boundaries reassigns the group (Group is a plain field, so the
        // grouped view is refreshed explicitly below).
        if (!string.Equals(dragged.Group, target.Group, System.StringComparison.Ordinal))
            dragged.Group = target.Group;

        list.Move(from, to);
        vm.Store.Save();
        ((CollectionViewSource)Resources["GroupedAccounts"]).View?.Refresh();
    }

    private Account? ItemUnder(DependencyObject? source)
        => FindAncestor<ListViewItem>(source)?.DataContext as Account;

    // Walks up the visual tree, falling back to the logical tree for ContentElements (e.g. a Run
    // inside a TextBlock) which VisualTreeHelper.GetParent cannot handle.
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = (current is Visual || current is Visual3D)
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
