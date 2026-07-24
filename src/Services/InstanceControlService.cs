using System.Diagnostics;
using System.Globalization;

namespace RobloxAccountManager.Services;

/// <summary>
/// The read/act API over the running Roblox clients. <see cref="ProcessRegistry"/> knows *which*
/// clients exist but exposes no actions, and now that website-launched clients are adopted into it
/// too, the UI needs one place that can both list them with live stats and act on a single one.
/// Pull-only by design: no timer, no background thread — callers refresh when they need to, so a
/// closed instances panel costs nothing.
/// </summary>
public static class InstanceControlService
{
    /// <summary>
    /// A point-in-time view of one client, safe to bind straight to the UI. Values are copied at
    /// <see cref="Snapshot"/> time rather than read live, so a row can't change mid-render or throw
    /// on a process that died between two property reads.
    /// </summary>
    public sealed class ClientInfo
    {
        public int Pid { get; init; }
        public long UserId { get; init; }
        public string Alias { get; init; } = "";
        public long PlaceId { get; init; }
        public string? JobId { get; init; }
        public TimeSpan Uptime { get; init; }
        public long MemoryBytes { get; init; }

        /// <summary>True for clients adopted from outside the manager (website Play, shortcut).</summary>
        public bool IsExternal { get; init; }

        /// <summary>False while the client is still on the splash screen — window actions will no-op.</summary>
        public bool HasWindow { get; init; }

        /// <summary>
        /// Human-sized working set for the list. Invariant culture on purpose: the number is a
        /// compact status glyph, not a localized figure, and a comma decimal reads as a thousands
        /// separator at a glance ("1,8 GB").
        /// </summary>
        public string MemoryText
        {
            get
            {
                const double Mb = 1024d * 1024d;
                const double Gb = Mb * 1024d;

                // -1 is ProcessRegistry's "pid is gone"; a dash beats a misleading "0 MB".
                if (MemoryBytes <= 0) return "—";

                return MemoryBytes >= Gb
                    ? (MemoryBytes / Gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                    : (MemoryBytes / Mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
            }
        }

        /// <summary>
        /// Coarse "how long has this been up" for the list — always two units at most, because the
        /// exact second stops mattering once a session is hours old.
        /// </summary>
        public string UptimeText
        {
            get
            {
                // Adopted clients take their start time from the OS, so a clock change (or DST
                // shift) can hand us a negative span. Clamp instead of printing "-1h 59m".
                var u = Uptime < TimeSpan.Zero ? TimeSpan.Zero : Uptime;

                if (u.TotalHours >= 1) return $"{(int)u.TotalHours}h {u.Minutes}m";
                if (u.TotalMinutes >= 1) return $"{u.Minutes}m {u.Seconds}s";
                return $"{u.Seconds}s";
            }
        }
    }

    /// <summary>
    /// Every live client with its current stats, newest launch first (the one the user just
    /// started is the one they are looking for). Never throws and never returns null.
    /// </summary>
    public static IReadOnlyList<ClientInfo> Snapshot()
    {
        var list = new List<ClientInfo>();
        try
        {
            // Explicit prune so a crashed client can't show up as a live row even if All's own
            // pruning behaviour ever changes.
            ProcessRegistry.Prune();

            foreach (var t in ProcessRegistry.All)
            {
                list.Add(new ClientInfo
                {
                    Pid = t.Pid,
                    UserId = t.UserId,
                    Alias = t.Alias,
                    PlaceId = t.PlaceId,
                    JobId = t.JobId,
                    Uptime = t.Uptime,
                    MemoryBytes = ProcessRegistry.MemoryBytes(t.Pid),
                    IsExternal = t.IsExternal,
                    HasWindow = ProcessRegistry.WindowHandle(t.Pid) != IntPtr.Zero,
                });
            }

            // Uptime, not LaunchedUtc: same ordering, but it already accounts for adopted clients
            // whose start time came from the OS.
            list.Sort((a, b) => a.Uptime.CompareTo(b.Uptime));
        }
        catch { /* a client exiting mid-enumeration must not break the panel */ }

        return list;
    }

    /// <summary>How many clients are currently tracked (dead ones are dropped on read).</summary>
    public static int Count
    {
        get { try { return ProcessRegistry.All.Count; } catch { return 0; } }
    }

    /// <summary>
    /// Brings one client to the front. Returns false when it has no window yet (still loading) or
    /// already exited — the caller decides whether that is worth telling the user about.
    /// </summary>
    public static bool Focus(int pid)
    {
        try
        {
            var hWnd = ProcessRegistry.WindowHandle(pid);
            if (hWnd == IntPtr.Zero) return false;

            Win32.ForceForeground(hWnd);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Closes one client: asks politely first so Roblox can flush its own state, then forces it.
    /// Blocks up to ~4.5s worst case (1.5s polite wait, then 3s for the kill to land), so batch
    /// callers must run off the UI thread. Returns false when there was nothing of ours to close
    /// or the process refused to die.
    /// </summary>
    public static bool Close(int pid)
    {
        bool closed = false;
        try
        {
            using var p = Process.GetProcessById(pid);

            // Windows recycles PIDs. Without this name check a stale registry entry could kill
            // whatever unrelated process inherited the number.
            bool isClient = !p.HasExited &&
                            p.ProcessName.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase);

            if (!isClient)
            {
                // The pid exists but isn't a client, so Prune() will never clear it — drop it here.
                ProcessRegistry.Forget(pid);
                return false;
            }

            p.CloseMainWindow();
            closed = p.WaitForExit(1500);
            if (!closed)
            {
                try { p.Kill(); } catch { /* raced with its own exit */ }

                // Only claim success once it is really gone. An elevated or protected client can
                // survive Kill(), and Forget()ting one that is still running would strand it:
                // Anti-AFK, the RAM monitor and the watchdog would all stop seeing it, and the
                // panel would have no row left to close it from.
                closed = p.WaitForExit(3000);
            }
        }
        catch { /* gone or access denied — nothing to close */ }

        if (closed)
        {
            // Drop it now rather than waiting for the next Prune, so the list updates immediately.
            ProcessRegistry.Forget(pid);
            AuditLogService.Log(AuditLogService.Category.Launch, $"Closed client pid {pid}.");
        }
        return closed;
    }

    /// <summary>
    /// Closes every client belonging to one account and returns how many actually stopped.
    /// Note: adopted external clients are registered under user id 0, so <c>CloseFor(0)</c> closes
    /// all of those at once.
    /// </summary>
    public static int CloseFor(long userId)
    {
        int closed = 0;
        try
        {
            // Materialise first: Close() mutates the registry as we go.
            var pids = ProcessRegistry.ForUser(userId).Select(t => t.Pid).ToList();
            foreach (int pid in pids)
                if (Close(pid)) closed++;
        }
        catch { }

        if (closed > 0)
            AuditLogService.Log(AuditLogService.Category.Launch, $"Closed {closed} client(s) for user {userId}.");
        return closed;
    }

    /// <summary>
    /// Closes every Roblox client on the machine, tracked or not. Delegates to
    /// <see cref="LauncherService.CloseAllClients"/> so the hotkey and this panel can never drift apart.
    /// </summary>
    public static int CloseAll()
    {
        try
        {
            // Snapshot the tracked pids *before* the kill. Reading ProcessRegistry.All afterwards
            // prunes the now-dead entries, and Prune raises ProcessRegistry.Exited — which the
            // watchdog reads as a crash and answers by auto-rejoining (plus a Discord
            // "Disconnected" and a crash toast) every account the user just asked to close.
            var tracked = ProcessRegistry.All.Select(t => t.Pid).ToList();

            int closed = LauncherService.CloseAllClients();

            // CloseAllClients kills processes directly, so the registry still holds their entries.
            // Forget(), never Prune(): Forget drops an entry silently, which is exactly what a
            // deliberate close needs. Survivors stay tracked so the other services keep seeing them.
            foreach (int pid in tracked)
            {
                bool gone;
                try { using var p = Process.GetProcessById(pid); gone = p.HasExited; }
                catch { gone = true; } // pid no longer resolves — it's gone
                if (gone) ProcessRegistry.Forget(pid);
            }

            if (closed > 0)
                AuditLogService.Log(AuditLogService.Category.Launch, $"Closed all {closed} Roblox client(s).");
            return closed;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Tiles every client window into a near-square grid across the primary monitor so a
    /// multi-account session is readable without dragging windows around. Returns how many moved.
    /// </summary>
    public static int ArrangeGrid()
    {
        var windows = LiveWindows();
        if (windows.Count == 0)
        {
            // Actionable: the user pressed a button and nothing happened. Explain why.
            if (Count > 0)
                ToastService.Warning("Nothing to arrange", "Roblox is still starting up — those clients have no window yet.");
            return 0;
        }

        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(windows.Count)));
        int rows = Math.Max(1, (int)Math.Ceiling(windows.Count / (double)cols));

        (int sw, int sh) = ScreenSize();
        int cellW = sw / cols;
        int cellH = sh / rows;

        int moved = 0;
        for (int i = 0; i < windows.Count; i++)
        {
            try
            {
                var hWnd = windows[i];

                // A minimized window silently ignores SetWindowPos' new bounds, so un-minimize first.
                Win32.RestoreIfMinimized(hWnd);

                int x = (i % cols) * cellW;
                int y = (i / cols) * cellH;

                // NOACTIVATE keeps focus where the user left it while a dozen windows shuffle.
                if (Win32.SetWindowPos(hWnd, IntPtr.Zero, x, y, cellW, cellH,
                        Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW))
                    moved++;
            }
            catch { /* window died mid-arrange — skip it */ }
        }
        return moved;
    }

    /// <summary>Minimizes every client window (get them out of the way). Returns how many were hidden.</summary>
    public static int MinimizeAll()
    {
        int n = 0;
        foreach (var hWnd in LiveWindows())
        {
            try { if (Win32.ShowWindow(hWnd, SW_MINIMIZE)) n++; }
            catch { }
        }
        return n;
    }

    /// <summary>Brings every client window back from the taskbar. Returns how many were restored.</summary>
    public static int RestoreAll()
    {
        int n = 0;
        foreach (var hWnd in LiveWindows())
        {
            // SW_RESTORE (not RestoreIfMinimized) so a maximized client also returns to its
            // normal size — this is the deliberate mirror of MinimizeAll.
            try { if (Win32.ShowWindow(hWnd, Win32.SW_RESTORE)) n++; }
            catch { }
        }
        return n;
    }

    /// <summary>
    /// SW_MINIMIZE isn't declared in <see cref="Win32"/> and that file is owned elsewhere, so the
    /// one constant this service needs lives here. 6 (not SW_SHOWMINNOACTIVE) because the user
    /// expects focus to move on after minimizing everything.
    /// </summary>
    private const int SW_MINIMIZE = 6;

    /// <summary>Main-window handles of all tracked clients; ones still loading are skipped.</summary>
    private static List<IntPtr> LiveWindows()
    {
        var list = new List<IntPtr>();
        try
        {
            foreach (var t in ProcessRegistry.All)
            {
                var h = ProcessRegistry.WindowHandle(t.Pid);
                if (h != IntPtr.Zero) list.Add(h);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Primary-monitor size, with a sane fallback for RDP/headless sessions that report 0.</summary>
    private static (int w, int h) ScreenSize()
    {
        int w = 0, h = 0;
        try
        {
            w = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            h = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
        }
        catch { }

        if (w <= 0 || h <= 0) { w = 1920; h = 1080; }
        return (w, h);
    }
}
