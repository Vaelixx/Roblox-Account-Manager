using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RobloxAccountManager.Services;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private TrayIcon? _tray;
    private bool _reallyClose;

    public MainViewModel ViewModel => _vm;

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;

        Loaded += OnLoaded;
        StateChanged += (_, _) => { UpdateMaxIcon(); UpdateChromeForState(); };

        var s = SettingsService.Current;
        if (s.WindowWidth > 700) Width = s.WindowWidth;
        if (s.WindowHeight > 500) Height = s.WindowHeight;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Loaded fires before the window has painted its first frame. Anything here
        // that opens a modal dialog (e.g. the Chromium prompt in CheckOnStartup)
        // would block inside a nested message loop while the main window is still
        // invisible — making the app look like it never started. Yield until the
        // dispatcher is idle so the window is actually on screen first.
        await Dispatcher.InvokeAsync(() => { },
            System.Windows.Threading.DispatcherPriority.ContextIdle);

        LauncherService.EnsureMultiInstance(SettingsService.Current.EnableMultiInstance);

        RequirementsService.CheckOnStartup();

        // First-run / password gate handled in App bootstrap; here just refresh live data.
        if (_vm.Store.Accounts.Count > 0)
        {
            _vm.SetStatus("Loading account data…");
            await _vm.Store.RefreshLiveDataAsync();
            _vm.SetStatus($"{_vm.Store.Accounts.Count} account(s) loaded.");
        }
        else
        {
            _vm.SetStatus("No accounts yet — click Add account to get started.");
        }
    }

    // ---- borderless-window plumbing: keep maximize inside the work area ----
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        ApplyRoundedCorners(handle);
        SetupTray();
        UpdateChromeForState();
    }

    // ---- Windows 11 rounded corners on the borderless window ----
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch { }
    }

    // ---- system tray ----
    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon(
                "Roblox Account Manager",
                onOpen: ShowFromTray,
                onExit: () => { _reallyClose = true; Close(); });
        }
        catch { _tray = null; }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // bounce to front
    }

    private void UpdateChromeForState()
    {
        // When maximized WPF pushes the window slightly off-screen; compensate so nothing is clipped.
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Margin = new Thickness(7);
            RootBorder.BorderThickness = new Thickness(0);
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
        }
    }

    private const int WM_GETMINMAXINFO = 0x0024;

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
                    mmi.ptMinTrackSize.X = (int)MinWidth;
                    mmi.ptMinTrackSize.Y = (int)MinHeight;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
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
        if (MaxButton?.Content is System.Windows.Shapes.Path p)
            p.Data = (System.Windows.Media.Geometry)FindResource(
                WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var s = SettingsService.Current;
        if (WindowState == WindowState.Normal) { s.WindowWidth = Width; s.WindowHeight = Height; }
        SettingsService.Save();
        _vm.Store.Save();

        // Close-to-tray: the X button hides to the tray unless the user disabled it or chose Exit.
        if (s.MinimizeToTray && !_reallyClose)
        {
            e.Cancel = true;
            Hide();
            if (_tray != null) _tray.Visible = true;
            return;
        }

        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        base.OnClosing(e);
    }
}
