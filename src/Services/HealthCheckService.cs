using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using Microsoft.Win32;

namespace RobloxAccountManager.Services;

/// <summary>
/// Turns "launching does nothing" into a checklist.
///
/// Almost every launch failure users report is environmental rather than a bug in the manager:
/// Roblox isn't installed, another bootstrapper has hijacked the <c>roblox-player://</c> protocol,
/// the data folder is read-only (a Program Files install, or antivirus quarantine), the disk is
/// full, or there is no route to the Roblox API. All of those fail silently today — the click
/// simply does nothing. This service probes each one and hands the Settings page a list of plain
/// statements the user can act on, so "it's broken" becomes "line 2 is red and here's the fix".
///
/// Every probe is individually wrapped: a broken registry hive or a sleeping drive turns into a
/// failed <see cref="Check"/>, never an exception, so one bad environment can never stop the rest
/// of the checklist from rendering.
/// </summary>
public static class HealthCheckService
{
    // The whole run is budgeted at ~8 s so a Settings page can await it without a spinner that
    // outlives the user's patience. Only the network probe can actually take time, so it owns the
    // entire budget minus a slack allowance for the local disk/registry probes.
    private const int ApiTimeoutSeconds = 6;

    /// <summary>Below this, Roblox updates and crash dumps start failing in ways users blame on us.</summary>
    private const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    private const string NetworkHint =
        "No route to Roblox. Check your internet connection, VPN, firewall or hosts file — while this "
        + "is red, logins, thumbnails, presence and launching will all fail.";

    // One client for the lifetime of the app: a fresh HttpClient per run would leak sockets in
    // TIME_WAIT every time the user opens the Settings page.
    private static readonly HttpClient Http;

    private static IReadOnlyList<Check> _last = Array.Empty<Check>();

    // Monotonic run counter so a slow run that has been superseded cannot publish stale results
    // over a newer one (the user hitting "re-check" twice is enough to cause that).
    private static int _runId;

    // The overtaken-run test and the publish have to happen as one step, or a run that has
    // already lost the race can still win the write.
    private static readonly object PublishGate = new();

    static HealthCheckService()
    {
        // PooledConnectionLifetime matters more here than anywhere else in the app: this probe
        // exists to tell the user whether the network is fixed yet, and a singleton client with
        // the default infinite lifetime would keep answering from a connection (and DNS result)
        // resolved before they reconnected the VPN — reporting red long after it went green.
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(ApiTimeoutSeconds) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager-HealthCheck");
    }

    /// <summary>
    /// One line of the checklist. Deliberately dumb data: the Settings page binds straight to it,
    /// so nothing here may touch a service or the Dispatcher.
    /// </summary>
    public sealed class Check
    {
        /// <summary>Stable label the user can quote back in a bug report.</summary>
        public string Title { get; init; } = "";

        /// <summary>False means the user has something to fix — <see cref="Hint"/> says what.</summary>
        public bool Ok { get; init; }

        /// <summary>The evidence (version folder, handler name, free space) so the verdict is checkable.</summary>
        public string Detail { get; init; } = "";

        /// <summary>Remedy text; null when there is nothing for the user to do.</summary>
        public string? Hint { get; init; }

        /// <summary>Text badge for the row — kept out of XAML so the two states can never drift apart.</summary>
        public string StatusGlyph => Ok ? "OK" : "!";

        /// <summary>Lets the row collapse its hint block instead of showing an empty gap.</summary>
        public bool HasHint => !string.IsNullOrEmpty(Hint);
    }

    /// <summary>
    /// Result of the previous run, so the Settings page can render instantly on open and only
    /// then kick off a refresh. Empty until the first run completes.
    /// </summary>
    public static IReadOnlyList<Check> Last => Volatile.Read(ref _last);

    /// <summary>
    /// True only when a run has actually happened and everything passed — an empty
    /// <see cref="Last"/> must never read as "healthy", or the caller would hide a warning banner
    /// it has not earned the right to hide yet.
    /// </summary>
    public static bool AllOk
    {
        get
        {
            var last = Last;
            return last.Count > 0 && last.All(c => c.Ok);
        }
    }

    /// <summary>
    /// Raised when a run finishes, possibly on a thread-pool thread — subscribers that touch UI
    /// must marshal via <c>Application.Current?.Dispatcher</c> themselves. This is a static event,
    /// so a page that subscribes on navigation must unsubscribe on unload: otherwise every visit
    /// to the Settings page pins another dead page in memory and refreshes it forever.
    /// </summary>
    public static event Action? Completed;

    /// <summary>
    /// Runs the whole checklist. Never throws — including when the token is cancelled, in which
    /// case the partial list is returned and <see cref="Last"/> is left untouched, because a
    /// cancelled run means the page went away, not that the machine became unhealthy.
    /// </summary>
    public static async Task<IReadOnlyList<Check>> RunAsync(CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _runId);
        var results = new List<Check>(6);

        // The local probes are blocking disk and registry I/O. One hop onto the pool keeps the UI
        // thread free while a spun-down drive takes a second to answer.
        try
        {
            results.AddRange(await Task.Run(() => new[]
            {
                Safe("Roblox installed",            ProbeRobloxInstalled),
                Safe("Launch protocol registered",  ProbeProtocol),
                Safe("Data folder writable",        ProbeDataFolder),
                Safe("Free disk space",             ProbeDiskSpace),
            }, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { return results; }

        if (ct.IsCancellationRequested) return results;
        results.Add(await SafeAsync("Roblox API reachable", () => ProbeApiAsync(ct)).ConfigureAwait(false));

        if (ct.IsCancellationRequested) return results;
        results.Add(Safe("Multi-instance guard", ProbeSingleton));

        // A run that has been overtaken must not publish: the newer one has fresher truth. The
        // test and the write are locked together because testing outside the lock leaves a window
        // where the newer run publishes in between, and this one then overwrites it with stale rows.
        lock (PublishGate)
        {
            if (Volatile.Read(ref _runId) != id) return results;
            Volatile.Write(ref _last, results);
        }

        // Only failures are worth a log line — a green checklist every startup would bury the
        // entries that actually explain a support question three days later.
        var failed = results.Where(c => !c.Ok).ToList();
        if (failed.Count > 0)
        {
            AuditLogService.Log(AuditLogService.Category.Launch,
                $"Health check: {failed.Count} of {results.Count} checks failed — "
                + string.Join("; ", failed.Select(c => $"{c.Title}: {c.Detail}")));
        }

        // A subscriber that throws is not allowed to take the app down with it.
        try { Completed?.Invoke(); } catch { }

        return results;
    }

    // ---------------------------------------------------------------- probe wrappers

    /// <summary>
    /// Runs one probe and converts any escape — a locked hive, a vanished drive — into a failed
    /// row. The title lives here rather than inside the probe so the success and failure paths
    /// can never disagree about what the row is called.
    /// </summary>
    private static Check Safe(string title, Func<(bool Ok, string Detail, string? Hint)> probe)
    {
        try
        {
            var (ok, detail, hint) = probe();
            return new Check { Title = title, Ok = ok, Detail = detail, Hint = hint };
        }
        catch (Exception ex)
        {
            return new Check { Title = title, Ok = false, Detail = $"Check failed: {ex.Message}" };
        }
    }

    /// <summary>Async twin of <see cref="Safe"/> for the one probe that goes out to the network.</summary>
    private static async Task<Check> SafeAsync(string title, Func<Task<(bool Ok, string Detail, string? Hint)>> probe)
    {
        try
        {
            var (ok, detail, hint) = await probe().ConfigureAwait(false);
            return new Check { Title = title, Ok = ok, Detail = detail, Hint = hint };
        }
        catch (Exception ex)
        {
            return new Check { Title = title, Ok = false, Detail = $"Check failed: {ex.Message}" };
        }
    }

    // ---------------------------------------------------------------- probes

    /// <summary>
    /// Finds RobloxPlayerBeta.exe under the per-user and 32-bit Program Files version folders.
    /// Reports the newest one, because that is the build the protocol handler will actually run.
    /// </summary>
    private static (bool Ok, string Detail, string? Hint) ProbeRobloxInstalled()
    {
        var roots = new List<string>(2);
        AddVersionsRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddVersionsRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        string? newest = null;
        DateTime newestStamp = DateTime.MinValue;
        int found = 0;

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;

            // The whole loop is guarded, not just the call that builds the enumerator: lazy
            // enumeration faults on MoveNext too (a Roblox update deleting a version folder
            // mid-walk), and that escape would turn the entire row red even though the other
            // root already found a player. Anything counted so far survives.
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        string exe = Path.Combine(dir, "RobloxPlayerBeta.exe");
                        if (!File.Exists(exe)) continue;

                        found++;
                        var stamp = File.GetLastWriteTimeUtc(exe);
                        if (stamp <= newestStamp) continue;

                        newestStamp = stamp;
                        newest = Path.GetFileName(dir);
                    }
                    catch { /* one unreadable version folder must not hide the others */ }
                }
            }
            catch { /* half-removed install, or a root we are not allowed to list */ }
        }

        if (newest is null)
            return (false, "RobloxPlayerBeta.exe was not found in any Versions folder.",
                "Install Roblox from roblox.com/download and run any game once. Nothing here can launch "
                + "until the player exists on disk.");

        // Old builds linger after every update; say so rather than looking like a duplicate install.
        return (true, found > 1 ? $"{newest} (+{found - 1} older)" : newest, null);
    }

    private static void AddVersionsRoot(List<string> roots, string? baseDir)
    {
        if (!string.IsNullOrEmpty(baseDir)) roots.Add(Path.Combine(baseDir, "Roblox", "Versions"));
    }

    /// <summary>
    /// Reads the <c>roblox-player://</c> handler. Launching is a protocol hand-off, so if this key
    /// is empty or owned by a third-party bootstrapper, every launch from this app disappears into
    /// something else — the single most common cause of "clicking play does nothing".
    /// </summary>
    private static (bool Ok, string Detail, string? Hint) ProbeProtocol()
    {
        using var key = Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command");
        string command = key?.GetValue(null) as string ?? "";

        if (string.IsNullOrWhiteSpace(command))
            return (false, "No handler registered for roblox-player://",
                "Reinstall Roblox, or launch a game once from roblox.com, so it re-registers the protocol. "
                + "Until then launches from this app are dropped by Windows.");

        string exe = ExecutableFrom(command);
        string name = exe.Length == 0 ? command : Path.GetFileName(exe);

        // Bloxstrap, Fishstrap and friends take this key over deliberately, so this is a warning
        // rather than a failure — the user just needs to know who is really receiving the launch.
        string? hint = exe.Contains("roblox", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"“{name}” owns roblox-player:// — another launcher or bootstrapper has taken the "
              + "protocol over. That is fine if you installed it on purpose; otherwise reinstall Roblox to take it back.";

        return (true, name, hint);
    }

    /// <summary>
    /// First token of a registry command line, honouring quotes and dropping the arguments — the
    /// full command line is noise (and often leaks a user name) in a UI row.
    /// </summary>
    private static string ExecutableFrom(string command)
    {
        string s = command.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
        }

        int space = s.IndexOf(' ');
        return space > 0 ? s.Substring(0, space) : s;
    }

    /// <summary>
    /// Proves the data folder is writable by actually writing, because <c>Directory.Exists</c>
    /// says nothing about a folder under Program Files or one an antivirus has locked — and a
    /// silent failure here loses accounts and settings.
    /// </summary>
    private static (bool Ok, string Detail, string? Hint) ProbeDataFolder()
    {
        // Unique name: a second manager instance probing at the same moment must not delete the
        // file this one is still writing.
        string probe = Paths.InData($"healthcheck-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(probe, "ok");

            return (true, Paths.DataDir, null);
        }
        catch (Exception ex)
        {
            return (false, $"{Paths.DataDir} — {ex.Message}",
                "Accounts, settings and backups cannot be saved. Move the manager out of Program Files, "
                + "or exclude its folder from your antivirus.");
        }
        finally
        {
            // Cleanup sits outside the verdict on purpose. The write is the proof the folder is
            // writable; an on-access antivirus scanner routinely holds the handle a moment longer
            // and fails the delete, and folding that into the catch above would paint a healthy
            // folder red and send the user off reinstalling somewhere else for no reason.
            SweepProbeFiles(probe);
        }
    }

    /// <summary>
    /// Deletes this run's probe file plus any an earlier run could not — without the sweep a
    /// locked delete would leave one orphan per health check in the data folder forever. The age
    /// cut-off keeps a second manager instance's in-flight probe safe.
    /// </summary>
    private static void SweepProbeFiles(string current)
    {
        try { File.Delete(current); } catch { }

        try
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (string stale in Directory.EnumerateFiles(Paths.DataDir, "healthcheck-*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(stale) < cutoff) File.Delete(stale);
                }
                catch { /* someone else's probe, or still locked — the next run tries again */ }
            }
        }
        catch { /* the folder is unreadable, which the row above has already reported */ }
    }

    /// <summary>
    /// Free space on the drive that holds the data folder — the same drive backups, logs and the
    /// updater's download all land on.
    /// </summary>
    private static (bool Ok, string Detail, string? Hint) ProbeDiskSpace()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Paths.DataDir)) ?? "";
        if (root.Length == 0)
            return (false, "Could not identify the drive holding the data folder.", null);

        // A UNC data folder has no DriveInfo; the app still works, so do not fail the checklist.
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return (true, "Network location — free space is not measurable.", null);

        var drive = new DriveInfo(root);
        long free = drive.AvailableFreeSpace;   // quota-aware, unlike TotalFreeSpace
        string detail = (free / (1024d * 1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " GB free";

        return free >= MinFreeBytes
            ? (true, detail, null)
            : (false, detail,
               $"Less than 2 GB free on {drive.Name} — Roblox updates, crash dumps and backups will start "
               + "failing. Free some space before launching.");
    }

    /// <summary>
    /// One request to a public, unauthenticated endpoint. Confirms DNS, TLS and the route in a
    /// single shot without needing a cookie, so it is safe to run before the vault is unlocked.
    /// </summary>
    private static async Task<(bool Ok, string Detail, string? Hint)> ProbeApiAsync(CancellationToken ct)
    {
        // Own deadline linked to the caller's token: a socket that hangs must not stretch the run
        // past its budget, and closing the page must abort the request immediately.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ApiTimeoutSeconds));

        var sw = Stopwatch.StartNew();
        try
        {
            // ResponseHeadersRead: the status line is the whole answer, no reason to buffer a body.
            using var resp = await Http.GetAsync("https://users.roblox.com/v1/users/1",
                HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();

            string detail = $"HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds} ms";
            return resp.IsSuccessStatusCode
                ? (true, detail, null)
                : (false, detail,
                   "Roblox answered, but not with success — the service may be having an outage, or a "
                   + "proxy/content filter is rewriting the response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"No answer within {ApiTimeoutSeconds} s.", NetworkHint);
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled.", null);   // the caller went away; not the user's problem
        }
        catch (Exception ex)
        {
            return (false, ex.Message, NetworkHint);
        }
    }

    /// <summary>
    /// State of the multi-instance guard. Reported here because a blocked sweep is invisible
    /// otherwise: clients launched from the website silently fold into the running window.
    /// </summary>
    private static (bool Ok, string Detail, string? Hint) ProbeSingleton()
    {
        // Turning the feature off is a choice, not a fault — a red row here would train the user
        // to ignore the whole checklist.
        if (!SettingsService.Current.EnableMultiInstance)
            return (true, "Disabled in settings — one Roblox client at a time.", null);

        if (RobloxSingletonService.AccessDenied)
            return (false, "Windows blocked access to a running Roblox client.",
                "Restart the manager as administrator (Settings → Launch) or extra clients started "
                + "from the website will not open.");

        return RobloxSingletonService.MutexHeld
            ? (true, $"Mutex held, {RobloxSingletonService.TotalClosed} lock(s) cleared this session.", null)
            : (false, "Multi-instance is on, but the Roblox mutex is not held.",
               "Toggle multi-instance off and on in Settings → Launch before starting a second client, "
               + "or the second launch will be swallowed by the first.");
    }
}
