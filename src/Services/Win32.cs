using System.Runtime.InteropServices;

namespace RobloxAccountManager.Services;

/// <summary>
/// Thin P/Invoke layer for the window/input plumbing used by Anti-AFK and the
/// window-grid manager. Kept in one place so the unsafe surface is easy to audit.
/// </summary>
internal static class Win32
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);

    internal const int SW_RESTORE = 9;
    internal const int SW_SHOW = 5;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const int SM_CXSCREEN = 0;   // primary monitor width (physical px)
    internal const int SM_CYSCREEN = 1;   // primary monitor height (physical px)

    /// <summary>Restores a window if it is currently minimized; no-op otherwise.</summary>
    internal static void RestoreIfMinimized(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero && IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    // ---- SendInput (keyboard) ----
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    /// <summary>
    /// Brings a window to the foreground, defeating the usual SetForegroundWindow
    /// restrictions by briefly attaching to the target's input queue.
    /// </summary>
    internal static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint thisThread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(hWnd, out _);

            if (fgThread != targetThread) AttachThreadInput(thisThread, fgThread, true);
            SetForegroundWindow(hWnd);
            ShowWindow(hWnd, SW_SHOW);
            if (fgThread != targetThread) AttachThreadInput(thisThread, fgThread, false);
        }
        catch { try { SetForegroundWindow(hWnd); } catch { } }
    }

    /// <summary>Sends a full key press (down + up) by virtual-key code to the focused window.</summary>
    internal static void TapKey(ushort vk, int holdMs = 90)
    {
        var down = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } };
        var up = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(holdMs);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }
}
