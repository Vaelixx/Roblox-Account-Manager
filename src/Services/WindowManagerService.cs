namespace RobloxAccountManager.Services;

/// <summary>
/// Tiles all tracked Roblox client windows into an even grid across the primary monitor.
/// Pulls live window handles from <see cref="ProcessRegistry"/>; skips dead/handle-less
/// ones and restores any that are minimized before placing them.
/// </summary>
public static class WindowManagerService
{
    /// <summary>
    /// Arranges every live Roblox window into a near-square grid (cols = ceil(sqrt(n))).
    /// Returns how many windows were moved.
    /// </summary>
    public static int TileGrid()
    {
        var windows = CollectWindows();
        if (windows.Count == 0) return 0;

        int cols = (int)Math.Ceiling(Math.Sqrt(windows.Count));
        int rows = (int)Math.Ceiling(windows.Count / (double)cols);

        (int sw, int sh) = ScreenSize();
        int cellW = sw / cols;
        int cellH = sh / rows;

        for (int i = 0; i < windows.Count; i++)
        {
            int r = i / cols, c = i % cols;
            int x = c * cellW, y = r * cellH;

            var hWnd = windows[i];
            Win32.RestoreIfMinimized(hWnd);
            Win32.SetWindowPos(hWnd, IntPtr.Zero, x, y, cellW, cellH,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
        return windows.Count;
    }

    /// <summary>Stacks all Roblox windows at the same size in the top-left (cascade off a bit).</summary>
    public static int Cascade(int offset = 32)
    {
        var windows = CollectWindows();
        if (windows.Count == 0) return 0;

        (int sw, int sh) = ScreenSize();
        int w = (int)(sw * 0.6), h = (int)(sh * 0.6);

        for (int i = 0; i < windows.Count; i++)
        {
            int x = Math.Min(i * offset, sw - w);
            int y = Math.Min(i * offset, sh - h);
            var hWnd = windows[i];
            Win32.RestoreIfMinimized(hWnd);
            Win32.SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
        return windows.Count;
    }

    private static List<IntPtr> CollectWindows()
    {
        var list = new List<IntPtr>();
        foreach (var t in ProcessRegistry.All)
        {
            var h = ProcessRegistry.WindowHandle(t.Pid);
            if (h != IntPtr.Zero) list.Add(h);
        }
        return list;
    }

    private static (int w, int h) ScreenSize()
    {
        int w = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
        int h = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
        if (w <= 0 || h <= 0) { w = 1920; h = 1080; }   // headless / RDP fallback
        return (w, h);
    }
}
