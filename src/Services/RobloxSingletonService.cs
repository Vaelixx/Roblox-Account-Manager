using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace RobloxAccountManager.Services;

/// <summary>
/// Keeps Roblox's single-instance guard out of the way so several clients can run at once —
/// including clients this app did not start (the website Play button, the Roblox home screen,
/// a Discord invite, a desktop shortcut).
///
/// Roblox uses two separate guards and the classic trick only defeats the first:
///
///  1. <c>ROBLOX_singletonMutex</c> — a named mutex. Holding a handle open keeps the object
///     alive across client restarts. This is what the app has always done.
///  2. <c>ROBLOX_singletonEvent</c> — a named event that newer clients create *inside their own
///     process*. A second client that finds it alive hands its launch URI to the running client
///     and exits instead of opening a window. Nothing an outside process pre-creates changes
///     that, which is exactly why launching from the website "did nothing" while the manager's
///     own launches still worked (those pass a fresh auth ticket the running client accepts).
///
/// The fix for (2) is to close that event handle *inside* each running client, the same
/// technique Process Explorer performs by hand. Because clients we did not launch also need it,
/// a low-cost watcher re-sweeps whenever a new Roblox process appears, so a website launch is
/// treated no differently from one started here.
///
/// Everything is best-effort: Roblox's anti-tamper can refuse the process handle, in which case
/// <see cref="StatusText"/> explains what happened instead of the app failing silently.
/// </summary>
public static class RobloxSingletonService
{
    private const string MutexName = "ROBLOX_singletonMutex";
    private const string EventName = "ROBLOX_singletonEvent";

    /// <summary>
    /// Processes that can own the singleton event (player, bootstrapper, Store build). Matched
    /// exactly, never by prefix — this app's own executable is "Roblox Account Manager", so a
    /// "starts with Roblox" test makes it scan its own handle table and count itself as a client.
    /// </summary>
    private static readonly string[] ClientProcessNames =
    {
        "RobloxPlayerBeta", "RobloxPlayerLauncher", "Windows10Universal",
    };

    // Re-sweep at least this often even when the process set looks unchanged: a client can
    // recreate the event after ours was closed (re-login, teleport, bootstrapper hand-off).
    private static readonly TimeSpan ForcedSweepInterval = TimeSpan.FromSeconds(10);

    private static readonly object _gate = new();
    private static Timer? _watcher;
    private static int _watchSeconds;             // current watcher period, 0 when stopped
    private static int _sweeping;                 // 0/1 re-entrancy guard for the timer callback
    private static DateTime _lastSweepUtc = DateTime.MinValue;
    private static readonly HashSet<int> _cleaned = new();   // pids already swept at least once

    // ---- mutex holder (own thread: Mutex ownership has thread affinity) ----
    private static Thread? _mutexThread;
    private static ManualResetEventSlim? _mutexStop;
    private static volatile bool _mutexOpen;

    // ---- observable state for the settings page ----
    private static volatile string _status = "Not started.";
    private static int _totalClosed;
    private static volatile bool _accessDenied;
    private static bool _warned;                  // one-shot "restart as admin" nudge

    /// <summary>Raised (possibly off the UI thread) whenever <see cref="StatusText"/> changed.</summary>
    public static event Action? StatusChanged;

    /// <summary>Human-readable one-liner describing the current guard state.</summary>
    public static string StatusText => _status;

    /// <summary>True while a handle to <c>ROBLOX_singletonMutex</c> is held open.</summary>
    public static bool MutexHeld => _mutexOpen;

    /// <summary>How many singleton-event handles have been closed since the app started.</summary>
    public static int TotalClosed => Volatile.Read(ref _totalClosed);

    /// <summary>True when a sweep was blocked by Windows — usually fixed by running as admin.</summary>
    public static bool AccessDenied => _accessDenied;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Starts or stops both guards to match the current settings. Safe to call repeatedly.</summary>
    public static void Apply()
    {
        var s = SettingsService.Current;
        if (!s.EnableMultiInstance) { Stop(); SetStatus("Multi-instance is off."); return; }

        EnsureMutex(true);

        if (s.CloseSingletonEvent)
        {
            // Apply() also runs before every launch, so only pay for a forced sweep when the
            // watcher actually (re)starts — otherwise a batch launch would queue one per account.
            if (StartWatcher(Math.Clamp(s.SingletonWatchSeconds, 1, 60)))
                _ = Task.Run(() => { try { Sweep(force: true); } catch { } });
        }
        else
        {
            StopWatcher();
            SetStatus(_mutexOpen
                ? "Mutex held. Clients started outside the app may still reuse a running window."
                : "Multi-instance could not take the Roblox mutex.");
        }
    }

    /// <summary>
    /// Relaunches the manager through the UAC prompt so the sweep can reach clients Windows
    /// currently refuses. The caller is expected to shut this instance down on success — the
    /// new process passes <c>--restart</c> and waits for the single-instance mutex to free.
    /// Returns false when the prompt is dismissed or the executable path is unknown.
    /// </summary>
    public static bool RestartElevated()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--restart");
            return Process.Start(psi) != null;
        }
        catch { return false; }   // ERROR_CANCELLED (1223) when the user dismisses UAC
    }

    /// <summary>Releases both guards. Called on shutdown.</summary>
    public static void Stop()
    {
        StopWatcher();
        EnsureMutex(false);
    }

    /// <summary>
    /// Legacy entry point kept for callers that only know about the mutex
    /// (<see cref="LauncherService"/> calls this right before every launch).
    /// </summary>
    public static void EnsureMutex(bool enabled)
    {
        lock (_gate)
        {
            if (enabled)
            {
                if (_mutexThread is { IsAlive: true }) return;

                var stop = new ManualResetEventSlim(false);
                _mutexStop = stop;
                _mutexThread = new Thread(() => HoldMutex(stop))
                {
                    IsBackground = true,
                    Name = "roblox-singleton-mutex",
                };
                _mutexThread.Start();
            }
            else
            {
                _mutexStop?.Set();
                _mutexStop = null;
                _mutexThread = null;
                _mutexOpen = false;
            }
        }
    }

    /// <summary>
    /// Owns the named mutex for the lifetime of the app. Runs on its own thread because
    /// <see cref="Mutex.ReleaseMutex"/> throws when called from a thread that does not own it —
    /// and the old code acquired it from whichever thread happened to run a launch.
    /// </summary>
    private static void HoldMutex(ManualResetEventSlim stop)
    {
        Mutex? mutex = null;
        bool owned = false;
        try
        {
            mutex = new Mutex(true, MutexName, out bool createdNew);
            if (createdNew)
            {
                owned = true;
            }
            else
            {
                // A client is already running and owns it. We only need the handle to stay
                // open so the object survives that client exiting; ownership is a bonus.
                try { owned = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { owned = true; }
                catch { owned = false; }
            }

            _mutexOpen = true;
            stop.Wait();
        }
        catch (Exception ex)
        {
            _mutexOpen = false;
            SetStatus($"Could not hold the Roblox mutex: {ex.Message}");
        }
        finally
        {
            _mutexOpen = false;
            if (owned) { try { mutex?.ReleaseMutex(); } catch { } }
            try { mutex?.Dispose(); } catch { }
            try { stop.Dispose(); } catch { }
        }
    }

    /// <summary>Starts (or re-times) the watcher. True when it was actually (re)started.</summary>
    private static bool StartWatcher(int seconds)
    {
        lock (_gate)
        {
            if (_watcher != null && _watchSeconds == seconds) return false;

            var period = TimeSpan.FromSeconds(seconds);
            _watchSeconds = seconds;
            if (_watcher == null)
                _watcher = new Timer(_ => { try { Sweep(); } catch { } }, null, TimeSpan.Zero, period);
            else
                _watcher.Change(TimeSpan.Zero, period);
            return true;
        }
    }

    private static void StopWatcher()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _watchSeconds = 0;
        }
        lock (_cleaned) _cleaned.Clear();
    }

    // ---------------------------------------------------------------- sweeping

    /// <summary>
    /// Runs one sweep on the caller's thread and reports what happened. Used by the
    /// "Fix multi-instance now" button so the user gets immediate feedback.
    /// </summary>
    public static int SweepNow(out string message)
    {
        int closed;
        try { closed = Sweep(force: true); }
        catch (Exception ex) { message = $"Sweep failed: {ex.Message}"; return 0; }

        if (closed > 0) message = $"Cleared {closed} Roblox instance lock(s) — launching from the website works now.";
        else if (_accessDenied) message = "Windows blocked access to the Roblox process. Restart the manager as administrator.";
        else if (ClientPids().Count == 0) message = "No Roblox client is running — nothing to clear.";
        else message = "Roblox is already unlocked for multi-instance.";
        return closed;
    }

    /// <summary>
    /// Closes every <c>ROBLOX_singletonEvent</c> handle held by a running Roblox process.
    /// Cheap when nothing changed: unless <paramref name="force"/> is set, processes already
    /// swept are skipped until <see cref="ForcedSweepInterval"/> elapses.
    /// </summary>
    private static int Sweep(bool force = false)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return 0;   // a sweep is already running
        try
        {
            var pids = ClientPids();
            if (pids.Count == 0)
            {
                lock (_cleaned) _cleaned.Clear();
                SetStatus(_mutexOpen
                    ? "Ready — no Roblox client running."
                    : "Ready, but the Roblox mutex is not held.");
                return 0;
            }

            // Clients we did not launch are now first-class: hand them to the registry so
            // Anti-AFK, the RAM monitor and the window grid can reach them as well. Done before
            // the "nothing new to sweep" shortcut below so an adoption is never skipped.
            if (SettingsService.Current.AdoptExternalClients)
            {
                try { ProcessRegistry.AdoptUntracked(); } catch { }
            }

            bool stale = DateTime.UtcNow - _lastSweepUtc > ForcedSweepInterval;
            List<int> targets;
            lock (_cleaned)
            {
                _cleaned.IntersectWith(pids);   // drop pids that have since exited
                targets = (force || stale) ? pids : pids.Where(p => !_cleaned.Contains(p)).ToList();
            }
            if (targets.Count == 0) return 0;

            _lastSweepUtc = DateTime.UtcNow;
            bool denied = false;
            int closed = 0;

            foreach (int pid in targets)
            {
                var (n, wasDenied) = CloseEventsIn(pid);
                closed += n;
                denied |= wasDenied;
                if (!wasDenied) lock (_cleaned) _cleaned.Add(pid);
            }

            _accessDenied = denied;
            if (closed > 0)
            {
                Interlocked.Add(ref _totalClosed, closed);
                AuditLogService.Log(AuditLogService.Category.Launch,
                    $"Multi-instance: cleared {closed} Roblox singleton lock(s)");
            }

            SetStatus(denied
                ? $"Blocked by Windows on {pids.Count} client(s) — restart as administrator to fix."
                : $"Active — {pids.Count} Roblox client(s) unlocked, {TotalClosed} lock(s) cleared this session.");

            // Say it once. Silently doing nothing is the failure mode users can't diagnose,
            // and repeating it every two seconds would be worse than saying nothing at all.
            if (denied && !_warned && SettingsService.Current.MultiInstanceStartupCheck)
            {
                _warned = true;
                ToastService.Warning("Multi-instance blocked",
                    "Windows won't let the manager unlock Roblox. Restart it as administrator "
                    + "(Settings → Launch) or extra clients started from the website won't open.");
            }
            return closed;
        }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    /// <summary>PIDs of every running process that could hold the singleton event.</summary>
    private static List<int> ClientPids()
    {
        var pids = new List<int>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return pids; }

        int self = Environment.ProcessId;
        foreach (var p in all)
        {
            try
            {
                if (p.Id == self) continue;
                string name = p.ProcessName;
                foreach (var client in ClientProcessNames)
                {
                    if (!string.Equals(name, client, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!p.HasExited) pids.Add(p.Id);
                    break;
                }
            }
            catch { /* process vanished mid-enumeration */ }
            finally { try { p.Dispose(); } catch { } }
        }
        return pids;
    }

    /// <summary>
    /// Closes the singleton event inside one process. Returns the number closed and whether
    /// Windows denied us the process handle (the one failure the user can actually act on).
    /// </summary>
    private static (int closed, bool accessDenied) CloseEventsIn(int pid)
    {
        IntPtr proc = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION, false, pid);
        if (proc == IntPtr.Zero)
        {
            // Some builds only grant the limited query right; duplication still works.
            proc = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return (0, err == ERROR_ACCESS_DENIED);
            }
        }

        try
        {
            var handles = ProcessHandles(proc, pid);
            if (handles == null) return (0, false);   // process gone or table unreadable

            int eventType = EventTypeIndex();
            int closed = 0;

            foreach (var (handle, typeIndex) in handles)
            {
                // Filter by object type first: name queries on the wrong object type can block.
                if (eventType >= 0 && typeIndex != eventType) continue;

                if (!DuplicateHandle(proc, handle, CurrentProcess, out IntPtr copy, 0, false, DUPLICATE_SAME_ACCESS))
                    continue;

                bool match = false;
                try
                {
                    if (eventType < 0 && !string.Equals(ObjectTypeName(copy), "Event", StringComparison.Ordinal))
                        continue;
                    var name = ObjectName(copy);
                    match = name != null && name.EndsWith(EventName, StringComparison.OrdinalIgnoreCase);
                }
                finally { CloseHandle(copy); }

                if (!match) continue;

                // DUPLICATE_CLOSE_SOURCE closes the handle in the *target* process; the copy we
                // get back is ours to close immediately.
                if (DuplicateHandle(proc, handle, CurrentProcess, out IntPtr dead, 0, false, DUPLICATE_CLOSE_SOURCE))
                {
                    CloseHandle(dead);
                    closed++;
                }
            }
            return (closed, false);
        }
        catch { return (0, false); }
        finally { CloseHandle(proc); }
    }

    private static void SetStatus(string text)
    {
        if (_status == text) return;
        _status = text;
        try { StatusChanged?.Invoke(); } catch { }
    }

    // ---------------------------------------------------------------- native plumbing

    private const int ProcessHandleInformation = 51;          // Win8+: one process' handle table
    private const int SystemExtendedHandleInformation = 64;   // fallback: every handle on the box
    private const int ObjectNameInformation = 1;
    private const int ObjectTypeInformation = 2;

    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint DUPLICATE_CLOSE_SOURCE = 0x0001;
    private const uint DUPLICATE_SAME_ACCESS = 0x0002;

    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int ERROR_ACCESS_DENIED = 5;

    private static readonly IntPtr CurrentProcess = GetCurrentProcess();

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr srcProcess, IntPtr srcHandle, IntPtr dstProcess,
                                               out IntPtr dstHandle, uint access,
                                               [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>PROCESS_HANDLE_TABLE_ENTRY_INFO — one row of a process' handle table.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessHandleEntry
    {
        public IntPtr HandleValue;
        public IntPtr HandleCount;
        public IntPtr PointerCount;
        public uint GrantedAccess;
        public uint ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    /// <summary>SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX — one row of the system-wide handle table.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleEntry
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    /// <summary>
    /// Handle table of one process. Uses the cheap per-process query (Windows 8+) and falls
    /// back to the system-wide snapshot on older builds. Null means "could not read it".
    /// </summary>
    private static unsafe List<(IntPtr handle, int typeIndex)>? ProcessHandles(IntPtr process, int pid)
    {
        int length = 64 * 1024;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                int status = NtQueryInformationProcess(process, ProcessHandleInformation, buffer, length, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    length = Math.Max(needed + 4096, length * 2);
                    if (length > 64 * 1024 * 1024) return null;
                    continue;
                }
                if (status < 0) return pid > 0 ? ProcessHandlesFallback(pid) : null;

                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                if (count <= 0 || count > 5_000_000) return null;

                var rows = new List<(IntPtr, int)>((int)Math.Min(count, 65536));
                var entries = (ProcessHandleEntry*)((byte*)buffer + 2 * IntPtr.Size);
                for (long i = 0; i < count; i++)
                    rows.Add((entries[i].HandleValue, (int)entries[i].ObjectTypeIndex));
                return rows;
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    /// <summary>System-wide handle snapshot filtered to one pid (pre-Windows 8 path).</summary>
    private static unsafe List<(IntPtr handle, int typeIndex)>? ProcessHandlesFallback(int pid)
    {
        int length = 4 * 1024 * 1024;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, length, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    length = Math.Max(needed + (1 << 20), length * 2);
                    if (length > 512 * 1024 * 1024) return null;
                    continue;
                }
                if (status < 0) return null;

                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                if (count <= 0 || count > 20_000_000) return null;

                var rows = new List<(IntPtr, int)>();
                var entries = (SystemHandleEntry*)((byte*)buffer + 2 * IntPtr.Size);
                for (long i = 0; i < count; i++)
                {
                    if (entries[i].UniqueProcessId.ToInt64() != pid) continue;
                    rows.Add((entries[i].HandleValue, entries[i].ObjectTypeIndex));
                }
                return rows;
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    // Object type indices are stable for the lifetime of a boot, so resolve once.
    private static int _eventTypeIndex = int.MinValue;

    /// <summary>
    /// The kernel's object-type index for Event, found by creating one ourselves and looking it
    /// up in our own handle table. Lets the sweep skip non-event handles without querying names.
    /// Returns -1 when it cannot be determined (the sweep then falls back to type-name checks).
    /// </summary>
    private static int EventTypeIndex()
    {
        if (_eventTypeIndex != int.MinValue) return _eventTypeIndex;

        int resolved = -1;
        try
        {
            using var probe = new EventWaitHandle(false, EventResetMode.ManualReset);
            IntPtr probeHandle = probe.SafeWaitHandle.DangerousGetHandle();
            var rows = ProcessHandles(CurrentProcess, Environment.ProcessId);
            if (rows != null)
                foreach (var (handle, typeIndex) in rows)
                    if (handle == probeHandle) { resolved = typeIndex; break; }
        }
        catch { resolved = -1; }

        _eventTypeIndex = resolved;
        return resolved;
    }

    /// <summary>Kernel object name of a handle we own, e.g. <c>\Sessions\1\BaseNamedObjects\…</c>.</summary>
    private static string? ObjectName(IntPtr handle) => QueryUnicodeString(handle, ObjectNameInformation);

    /// <summary>Object type name of a handle we own, e.g. <c>Event</c>.</summary>
    private static string? ObjectTypeName(IntPtr handle) => QueryUnicodeString(handle, ObjectTypeInformation);

    /// <summary>
    /// Reads the leading UNICODE_STRING out of an NtQueryObject result. Both
    /// OBJECT_NAME_INFORMATION and OBJECT_TYPE_INFORMATION start with one.
    /// </summary>
    private static string? QueryUnicodeString(IntPtr handle, int infoClass)
    {
        int length = 2048;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                buffer = Marshal.AllocHGlobal(length);
                int status = NtQueryObject(handle, infoClass, buffer, length, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH && needed > length)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    length = needed;
                    if (length > 1 << 20) return null;
                    continue;
                }
                if (status < 0) return null;

                ushort byteLength = (ushort)Marshal.ReadInt16(buffer, 0);
                IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                if (byteLength == 0 || text == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(text, byteLength / 2);
            }
            return null;
        }
        catch { return null; }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }
}
