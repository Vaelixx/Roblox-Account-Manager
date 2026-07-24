using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RobloxAccountManager.Services;

/// <summary>
/// Central sink for the failures this app deliberately swallows. Nearly every background
/// helper here catches broadly so a transient error can never kill the process — correct for
/// stability, but it leaves the user staring at a silent no-op with nothing to report.
/// Those catch blocks now have somewhere cheap to write to: an in-memory ring buffer the
/// Settings page can render with no disk IO, plus a rotating <c>data/diagnostics.log</c>
/// that survives a crash and can be attached to a bug report.
/// <para>
/// Contract: nothing on this type ever throws, and nothing on it depends on settings having
/// been loaded — it must be usable from the first line of startup and from inside a
/// <c>catch</c> whose whole point is that it cannot fail.
/// </para>
/// </summary>
public static class DiagnosticsService
{
    // One gate for the ring buffer AND the file, so a Settings page reading Recent() can never
    // interleave with a background thread rotating the log out from under an append.
    private static readonly object Gate = new();

    private const int RingCapacity = 500;          // ~500 lines is plenty of context for a report
    private const long MaxLogBytes = 1024 * 1024;  // rotate past 1 MB so the file can't grow unbounded

    // The ring is bounded in ENTRIES, not bytes. Exception messages are attacker-sized in
    // practice (a whole HTTP body, a serialised payload), so without a per-field cap 500
    // entries can pin an unbounded amount of memory and defeat the 1 MB log cap in one write.
    private const int MaxFieldChars = 2000;

    private static readonly Queue<string> Ring = new(RingCapacity);

    private const string LevelInfo = "INFO";
    private const string LevelWarn = "WARN";
    private const string LevelError = "ERROR";

    private static int _errorCount;
    private static int _installed;

    /// <summary>
    /// Errors recorded since app start. Drives the "something went wrong" badge in Settings —
    /// the only hint a user gets that a swallowed failure happened at all.
    /// </summary>
    public static int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>
    /// Raised after every new entry (and after <see cref="Clear"/>) so a live log view can
    /// refresh. Fires on whichever thread logged — deliberately NOT marshalled, because
    /// logging must stay non-blocking; a handler touching bound state must hop to the UI
    /// thread itself via <c>System.Windows.Application.Current?.Dispatcher</c>.
    /// </summary>
    public static event Action? Changed;

    private static string? _logPath;

    /// <summary>Full path of the rotating log, for "open folder" / bug-report copy actions.</summary>
    public static string LogPath
    {
        get
        {
            // Resolved lazily rather than in a static initialiser: a throwing field initialiser
            // would poison the whole type with TypeInitializationException on every later call.
            try { return _logPath ??= Paths.InData("diagnostics.log"); }
            catch { return _logPath ?? ""; }
        }
    }

    /// <summary>
    /// Captures the two failure classes that otherwise vanish without a trace: exceptions that
    /// escape a background thread, and faulted fire-and-forget tasks nobody awaited. Idempotent,
    /// so a second call (post-update relaunch, tests) cannot double-hook and double-log.
    /// </summary>
    public static void Install()
    {
        try
        {
            if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0) return;

            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;

            // Banner first: it stamps the session boundary in a log that spans app restarts,
            // and reading Paths.DataDir here forces the portable/roaming decision to be
            // recorded before anything can fail over it.
            string version = "?";
            try { version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?"; }
            catch { }
            Log("app", $"session start — v{version}, {Environment.OSVersion.VersionString}, data={Paths.DataDir}");
        }
        catch { /* diagnostics failing to install must never block startup */ }
    }

    /// <summary>Records routine progress. <paramref name="area"/> is a short tag like "launcher".</summary>
    public static void Log(string area, string message) => Write(LevelInfo, area, message, null);

    /// <summary>Records a recoverable problem — the operation continued, possibly degraded.</summary>
    public static void Warn(string area, string message, Exception? ex = null) => Write(LevelWarn, area, message, ex);

    /// <summary>Records a failed operation. Counts toward <see cref="ErrorCount"/>.</summary>
    public static void Error(string area, string message, Exception? ex = null) => Write(LevelError, area, message, ex);

    /// <summary>
    /// The most recent <paramref name="count"/> entries, newest LAST (reading order).
    /// Served from memory so the Settings page can poll it without touching the disk.
    /// </summary>
    public static IReadOnlyList<string> Recent(int count = 200)
    {
        try
        {
            if (count <= 0) return Array.Empty<string>();
            lock (Gate)
            {
                int skip = Math.Max(0, Ring.Count - count);
                return Ring.Skip(skip).ToList();
            }
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Opens the data folder in Explorer so the user can grab the log for a bug report.
    /// Failure here is user-visible and actionable, so it toasts rather than staying silent.
    /// </summary>
    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            // Disposed even though we never wait on it: with UseShellExecute the shell may hand
            // back a live Process, and leaking one handle per click adds up in a tray app that
            // runs for days.
            using var explorer = Process.Start(new ProcessStartInfo(Paths.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn("diagnostics", "could not open the data folder", ex);
            try { ToastService.Error("Couldn't open the folder", Paths.DataDir); } catch { }
        }
    }

    /// <summary>
    /// Wipes the buffer, the log and the rotated copy, and resets the error badge.
    /// The <c>.1</c> file goes too — a user clearing diagnostics expects the old contents
    /// gone, not silently preserved next to it.
    /// </summary>
    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                Ring.Clear();
                string path = LogPath;
                if (path.Length > 0)
                {
                    TryDelete(path);
                    TryDelete(path + ".1");
                }
            }
            Interlocked.Exchange(ref _errorCount, 0);
            RaiseChanged();
        }
        catch { }
    }

    // ---------------------------------------------------------------
    //  Internals
    // ---------------------------------------------------------------

    private static void Write(string level, string area, string message, Exception? ex)
    {
        try
        {
            string line = Format(level, area, message, ex);

            lock (Gate)
            {
                Ring.Enqueue(line);
                while (Ring.Count > RingCapacity) Ring.Dequeue();
                TryAppend(line);
            }

            if (level == LevelError) Interlocked.Increment(ref _errorCount);

            // Outside the lock: a subscriber that logs (or blocks) must not deadlock the sink.
            RaiseChanged();
        }
        catch { /* the sink for swallowed errors cannot itself throw */ }
    }

    private static string Format(string level, string? area, string? message, Exception? ex)
    {
        // Invariant culture on purpose: under a non-Gregorian calendar (th-TH etc.) the default
        // formatter writes a different year, making timestamps in a bug report unusable.
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        string tag = Clean(area);
        if (tag.Length == 0) tag = "app";

        var sb = new StringBuilder(160);
        sb.Append(stamp).Append("Z  [").Append(level).Append("] [").Append(tag).Append("] ").Append(Clean(message));

        if (ex != null)
        {
            string type;
            string detail;
            try { type = ex.GetType().Name; } catch { type = "Exception"; }
            try { detail = Clean(ex.Message); } catch { detail = ""; }
            sb.Append(" :: ").Append(type).Append(": ").Append(detail);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Caps the length, collapses anything that could break the entry across lines, and strips
    /// anything cookie-shaped, so one entry is always exactly one bounded line and a log the
    /// user is about to email can never carry a .ROBLOSECURITY value.
    /// </summary>
    private static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try
        {
            int keep = Math.Min(s.Length, MaxFieldChars);
            // Don't split a surrogate pair — a lone half would encode as U+FFFD in the file.
            if (keep < s.Length && char.IsHighSurrogate(s[keep - 1])) keep--;
            int dropped = s.Length - keep;

            var sb = new StringBuilder(keep + 16);
            for (int i = 0; i < keep; i++)
            {
                char c = s[i];
                // CR/LF/TAB plus the rest of the control range and U+2028/U+2029, which plenty
                // of log viewers honour as line breaks and would fake an extra entry.
                sb.Append(c < ' ' || c == '\u007f' || c == '\u2028' || c == '\u2029' ? ' ' : c);
            }
            if (dropped > 0) sb.Append("… (+").Append(dropped).Append(" chars)");

            // Redaction runs on the truncated text on purpose: the scan below costs O(n²) on a
            // long run of non-whitespace, and truncation can only ever cut a cookie's leading
            // warning header — the secret tail lives after it, so it is gone either way.
            return CookieToken.Replace(sb.ToString(), RedactMatch);
        }
        catch { return ""; }   // never let sanitising be the thing that leaks or throws
    }

    // Matches the whole whitespace-delimited token a cookie lives in, not just the marker, so
    // the secret tail is removed along with it. The timeout is a fail-closed backstop: if the
    // scan ever blows its budget, Clean's catch drops the field entirely rather than emitting
    // text nothing verified.
    private static readonly Regex CookieToken =
        new(@"\S*WARNING:-DO-NOT-SHARE\S*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));

    private const string CookieMarker = "_|WARNING:-DO-NOT-SHARE-THIS";

    private static string RedactMatch(Match m)
    {
        string v = m.Value;
        // Either the real cookie prefix, or any long token carrying the warning text — both are
        // credentials. A short mention with neither trait is prose and stays readable.
        if (v.Contains(CookieMarker, StringComparison.Ordinal) || v.Length > 40) return "<cookie redacted>";
        return v;
    }

    /// <summary>Appends one line, rotating first if the file is at its cap. Caller holds <see cref="Gate"/>.</summary>
    private static void TryAppend(string line)
    {
        try
        {
            string path = LogPath;
            if (path.Length == 0) return;

            Directory.CreateDirectory(Paths.DataDir);

            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length + line.Length + 2 > MaxLogBytes)
            {
                // Keep exactly one generation back: enough to cover a crash that happened just
                // before a rotation, without an ever-growing pile of numbered logs.
                try { File.Move(path, path + ".1", overwrite: true); }
                catch { TryDelete(path); }   // rotation blocked (file locked) — losing history beats growing forever
            }

            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* disk full / read-only install: the ring buffer still has the entry */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    // Per-thread, because the recursion this guards against is always a straight re-entry on
    // the logging thread; a genuine concurrent log on another thread still gets its notification.
    [ThreadStatic] private static bool _raising;

    private static void RaiseChanged()
    {
        // A subscriber that logs — a refresh handler with its own swallowing catch calling
        // Warn(), say — would otherwise re-enter Write and recurse until the stack blows, and
        // StackOverflowException cannot be caught: the one class that promises it can never
        // kill the app would be the thing that kills it. Suppressing the nested notification
        // is free, since the outer one still fires after the nested entry is in the ring.
        if (_raising) return;
        _raising = true;
        try { Changed?.Invoke(); }
        catch { /* a broken subscriber must not break logging */ }
        finally { _raising = false; }
    }

    private static void OnDomainUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            string what = e.IsTerminating ? "unhandled exception (terminating)" : "unhandled exception";
            if (ex != null) Error("crash", what, ex);
            else Error("crash", $"{what} — non-Exception payload: {e.ExceptionObject?.GetType().FullName ?? "null"}");
        }
        catch { }
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            AggregateException? agg = e.Exception;
            Exception? inner = agg?.Flatten().InnerExceptions.FirstOrDefault() ?? agg;
            Error("task", "unobserved task exception", inner);
        }
        catch { }
        finally
        {
            // Always observe, even if recording failed: these are background helpers, and an
            // unobserved fault escalating on the finaliser thread would take the app down.
            try { e.SetObserved(); } catch { }
        }
    }
}
