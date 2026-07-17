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
