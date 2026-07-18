using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RobloxAccountManager.Services;

/// <summary>
/// Native system-tray icon (Shell_NotifyIcon) with an "Open / Exit" context menu.
/// Replaces the old WinForms NotifyIcon so the whole WinForms framework can be dropped
/// from the self-contained bundle. Lives on a hidden message-only window whose pump is
/// the WPF Dispatcher — all callbacks already run on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAY          = 0x8000 + 1;   // WM_APP + 1
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP     = 0x0205;
    private const int WM_CONTEXTMENU   = 0x007B;

    private const uint NIM_ADD    = 0x0;
    private const uint NIM_MODIFY = 0x1;
    private const uint NIM_DELETE = 0x2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON    = 0x2;
    private const uint NIF_TIP     = 0x4;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD   = 0x0100;
    private const uint MF_STRING    = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;

    private const int CMD_OPEN = 1;
    private const int CMD_EXIT = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, IntPtr id, string? item);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _source;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private readonly string _tip;
    private IntPtr _hIcon;
    private IntPtr _hMenu;
    private bool _visible;
    private bool _disposed;

    public TrayIcon(string tooltip, Action onOpen, Action onExit)
    {
        _tip = tooltip;
        _onOpen = onOpen;
        _onExit = onExit;

        // Message-only window (parent HWND_MESSAGE): invisible, no taskbar entry, pumped by the Dispatcher.
        var p = new HwndSourceParameters("RAM.TrayWindow")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3),   // HWND_MESSAGE
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        // Small (16px) frame of the exe's own icon — crisp in the tray.
        try
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            var small = new IntPtr[1];
            ExtractIconEx(exe, 0, null, small, 1);
            _hIcon = small[0];
        }
        catch { _hIcon = IntPtr.Zero; }

        _hMenu = CreatePopupMenu();
        AppendMenu(_hMenu, MF_STRING, (IntPtr)CMD_OPEN, "Open");
        AppendMenu(_hMenu, MF_SEPARATOR, IntPtr.Zero, null);
        AppendMenu(_hMenu, MF_STRING, (IntPtr)CMD_EXIT, "Exit");

        Visible = true;
    }

    /// <summary>Adds/removes the icon. Mirrors the old WinForms NotifyIcon.Visible contract.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_disposed || _visible == value) return;
            var data = MakeData();
            if (value) Shell_NotifyIcon(NIM_ADD, ref data);
            else Shell_NotifyIcon(NIM_DELETE, ref data);
            _visible = value;
        }
    }

    private NOTIFYICONDATA MakeData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAY,
        hIcon = _hIcon,
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            switch (lParam.ToInt32())
            {
                case WM_LBUTTONDBLCLK:
                    _onOpen();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        else if (msg == (int)WM_TASKBARCREATED && _visible)
        {
            // Explorer restarted — the tray was wiped, put the icon back.
            var data = MakeData();
            Shell_NotifyIcon(NIM_ADD, ref data);
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // Foreground + WM_NULL bounce is the documented dance so the menu closes on outside clicks.
        SetForegroundWindow(_source.Handle);
        GetCursorPos(out var pt);
        int cmd = TrackPopupMenuEx(_hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _source.Handle, IntPtr.Zero);
        PostMessage(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);   // WM_NULL

        if (cmd == CMD_OPEN) _onOpen();
        else if (cmd == CMD_EXIT) _onExit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Visible = false;
        _disposed = true;
        if (_hMenu != IntPtr.Zero) { DestroyMenu(_hMenu); _hMenu = IntPtr.Zero; }
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
