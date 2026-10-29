using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RobloxAccountManager.Services;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private TrayIcon? _tray;
    private bool _reallyClose;

    public MainViewModel ViewModel => _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;

        Loaded += OnLoaded;
        StateChanged += (_, _) => { UpdateMaxIcon(); UpdateChromeForState(); OnStateChangedForTray(); };

        var s = SettingsService.Current;
        if (s.WindowWidth >= MinWidth) Width = s.WindowWidth;
        if (s.WindowHeight >= MinHeight) Height = s.WindowHeight;
        // Back where it was, unless that spot is off every screen now (a monitor was unplugged).
        if (s.WindowLeft is double left && s.WindowTop is double top
            && left >= SystemParameters.VirtualScreenLeft && top >= SystemParameters.VirtualScreenTop
            && left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && top + 50 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        ApplySidebarWidth();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SidebarCollapsed)) ApplySidebarWidth();
        };
        ThemeService.Changed += ApplyBorderColor;
    }

    private void ApplySidebarWidth()
        => SidebarColumn.Width = new GridLength(_vm.SidebarCollapsed ? 60 : 236);

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Loaded fires before the first frame. A modal opened here (the Roblox-missing prompt) would
        // block in a nested loop while the window is still invisible, so yield until it has painted.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

        if (AppInfo.IsDemo) return;

        try
        {
            LauncherService.EnsureMultiInstance(SettingsService.Current.EnableMultiInstance);
            RequirementsService.CheckOnStartup();

            if (_vm.Store.Accounts.Count > 0)
            {
                _vm.SetStatus(L.T("Status.Loading"));
                await _vm.Store.RefreshLiveDataAsync();
                _vm.SetStatus(L.N("Status.Loaded", _vm.Store.Accounts.Count));
            }
            else
            {
                _vm.SetStatus(L.T("Status.Empty"));
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("ui", "Startup refresh failed", ex);
        }
    }

    // ---- borderless window plumbing ----
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
        ApplyBorderColor();
        SetupTray();
        UpdateChromeForState();
        UpdateMaxIcon();
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 11 draws the outer window border; tint it to the theme's hairline so it doesn't glow white.</summary>
    private void ApplyBorderColor()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            if (TryFindResource("HairlineStrong") is Color c)
            {
                int colorRef = c.R | (c.G << 8) | (c.B << 16);   // COLORREF is 0x00BBGGRR
                DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
            }
        }
        catch { }
    }

    // ---- tray ----
    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon(
                "Roblox Account Manager",
                onOpen: BringToFront,
                onExit: () => { _reallyClose = true; Close(); },
                onLock: () => LockService.Lock("tray"),
                onCloseClients: _vm.CloseAllClients);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("tray", "Tray icon could not be created", ex);
            _tray = null;
        }
    }

    /// <summary>Drops the window to the tray (autostart with "start minimized").</summary>
    public void HideToTray()
    {
        Hide();
        if (_tray != null) _tray.Visible = true;
        if (SettingsService.Current.LockOnMinimize) LockService.Lock("hidden");
    }

    /// <summary>Shows the window from the tray or behind other windows and gives it focus.</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;    // nudge past other windows…
        Topmost = false;   // …without actually pinning it there
        Focus();
    }

    private void OnStateChangedForTray()
    {
        if (WindowState == WindowState.Minimized && SettingsService.Current.LockOnMinimize)
            LockService.Lock("minimized");
    }

    private void UpdateChromeForState()
    {
        // WM_GETMINMAXINFO below places a maximized window exactly on the work area, so there is no
        // overhang to pad; an inset here only showed up as a dark frame around the maximized window.
        RootBorder.Margin = new Thickness(0);
        RootBorder.BorderThickness = new Thickness(0);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SETTINGCHANGE = 0x001A;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    RECT work = info.rcWork, mon = info.rcMonitor;
                    mmi.ptMaxPosition.X = work.Left - mon.Left;
                    mmi.ptMaxPosition.Y = work.Top - mon.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                    var source = PresentationSource.FromVisual(this);
                    double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                    double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                    mmi.ptMinTrackSize.X = (int)(MinWidth * scaleX);
                    mmi.ptMinTrackSize.Y = (int)(MinHeight * scaleY);
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        }
        else if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero)
        {
            // "ImmersiveColorSet" is broadcast when the Windows light/dark app setting flips.
            if (Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"
                && SettingsService.Current.ThemeMode == ThemeService.ModeSystem)
                ThemeService.Apply(SettingsService.Current);
        }
        return IntPtr.Zero;
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaxIcon()
    {
        MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");
        MaxButton.ToolTip = FindResource(WindowState == WindowState.Maximized ? "Str.Window.Restore" : "Str.Window.Maximize");
    }

    // ---- command palette ----
    private void Palette_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
            Dispatcher.BeginInvoke(new Action(() => { PaletteInput.Focus(); Keyboard.Focus(PaletteInput); }),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var palette = _vm.Palette;
        switch (e.Key)
        {
            case Key.Down: palette.Move(1); PaletteList.ScrollIntoView(palette.Selected); e.Handled = true; break;
            case Key.Up: palette.Move(-1); PaletteList.ScrollIntoView(palette.Selected); e.Handled = true; break;
            case Key.Enter: palette.RunCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: palette.IsOpen = false; e.Handled = true; break;
        }
    }

    private void PaletteList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControlFromItem(d) != null)
            _vm.Palette.RunCommand.Execute(null);
    }

    private static object? ItemsControlFromItem(DependencyObject d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.ListBoxItem item) return item;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void PaletteScrim_MouseDown(object sender, MouseButtonEventArgs e) => _vm.Palette.IsOpen = false;

    // ---- lock ----
    private void Lock_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            _vm.Palette.IsOpen = false;
            Dispatcher.BeginInvoke(new Action(() => UnlockBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // While locked, only the unlock box takes input — shortcuts must not reach the pages behind.
        if (_vm.IsLocked)
        {
            if (!UnlockBox.IsKeyboardFocusWithin) UnlockBox.Focus();
            // Window shortcuts (Ctrl+N, Ctrl+K, …) must not act on the pages behind the lock screen;
            // paste, select-all and undo still work inside the password box.
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key is not (Key.V or Key.A or Key.Z))
            {
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
            return;
        }
        if (_vm.Palette.IsOpen && e.Key == Key.Escape)
        {
            _vm.Palette.IsOpen = false;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var s = SettingsService.Current;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { s.WindowWidth = Width; s.WindowHeight = Height; s.WindowLeft = Left; s.WindowTop = Top; }
        SettingsService.Save();
        _vm.Store.Save();

        // Close-to-tray: the X hides to the tray unless that is turned off or Exit was chosen.
        if (s.MinimizeToTray && !_reallyClose && _tray != null)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        base.OnClosing(e);
    }
}
