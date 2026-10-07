using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace RobloxAccountManager.Services;

/// <summary>
/// Follows each tracked client's own log file and keeps a <see cref="ClientLogState"/> per pid.
///
/// Roblox writes one log per client process, created as the process starts. A client is matched
/// to the Player log created closest to its start time (within a few seconds) that no other client
/// holds; once the log names the account (on join), a mismatch drops the match and the log is
/// remembered as that other account's, so the next tick tries the next candidate instead of the
/// same file again. Crash-handler and installer logs are never matched: they never show a join, and
/// following one made a healthy client look stuck on "Starting" until it was restarted. Clients
/// without a matching log simply have no state, and nothing acts on them.
/// </summary>
public static class ClientLogWatcher
{
    private sealed class Follow
    {
        public required string Path;
        public required ClientLogState State;
        public long Offset;
        public string Pending = "";
    }

    private static readonly ConcurrentDictionary<int, Follow> _byPid = new();
    // Logs whose account is known (they named it on join): only a client of that account may follow them.
    private static readonly ConcurrentDictionary<string, long> _owner = new(StringComparer.OrdinalIgnoreCase);
    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    private static int _busy;

    /// <summary>Raised (off the UI thread) after every pass over the logs.</summary>
    public static event Action? Updated;

    private static string LogDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

    /// <summary>The log's view of a client, or null when no log could be matched to it.</summary>
    public static ClientLogState? StateOf(int pid) => _byPid.TryGetValue(pid, out var f) ? f.State : null;

    /// <summary>Follows the logs while something reads them: disconnect recovery, or Anti-AFK's loading help.</summary>
    public static void Apply()
    {
        var s = SettingsService.Current;
        if ((s.WatchdogEnabled && s.RejoinOnDisconnect) || (s.AntiAfkEnabled && s.AntiAfkHelpLoading)) Start();
        else Stop();
    }

    private static void Start()
    {
        lock (_gate)
            _timer ??= new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    private static void Stop()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
        _byPid.Clear();
        _owner.Clear();
    }

    private static void Tick()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var clients = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).ToList();
            foreach (int pid in _byPid.Keys.Except(clients.Select(t => t.Pid)).ToList()) _byPid.TryRemove(pid, out _);

            FileInfo[]? logs = null;
            foreach (var t in clients)
            {
                if (!_byPid.TryGetValue(t.Pid, out var f))
                {
                    if (logs == null)
                    {
                        logs = ListLogs();
                        var listed = logs.Select(l => l.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var gone in _owner.Keys.Where(k => !listed.Contains(k)).ToList()) _owner.TryRemove(gone, out _);
                    }
                    f = Match(t, logs);
                    if (f == null) continue;
                    _byPid[t.Pid] = f;
                }
                Read(f);
                if (f.State.UserId != 0) _owner[f.Path] = f.State.UserId;
                if (f.State.UserId != 0 && f.State.UserId != t.UserId)
                {
                    DiagnosticsService.Warn("logs", $"Log {System.IO.Path.GetFileName(f.Path)} belongs to another account, not {t.Alias}; matching again");
                    _byPid.TryRemove(t.Pid, out _);
                }
            }
            try { Updated?.Invoke(); } catch { }
        }
        catch (Exception ex) { DiagnosticsService.Warn("logs", "Reading Roblox logs failed", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static FileInfo[] ListLogs()
    {
        try
        {
            var dir = new DirectoryInfo(LogDir);
            if (!dir.Exists) return Array.Empty<FileInfo>();
            var cutoff = DateTime.Now.AddDays(-2);
            return dir.GetFiles("*.log")
                      .Where(f => ClientLogFiles.IsClientLog(f.Name) && f.CreationTime > cutoff)
                      .ToArray();
        }
        catch { return Array.Empty<FileInfo>(); }
    }

    private static Follow? Match(ProcessRegistry.Tracked t, FileInfo[] logs)
    {
        if (t.StartTimeLocal == default) return null;
        var held = _byPid.Values.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? path = ClientLogFiles.PickLog(logs.Select(f => (f.FullName, f.CreationTime)), t.StartTimeLocal, t.UserId, held, _owner);
        if (path == null) return null;

        DiagnosticsService.Log("logs", $"Following {System.IO.Path.GetFileName(path)} for {t.Alias} (pid {t.Pid})");
        return new Follow { Path = path, State = new ClientLogState(t.StartTimeLocal.ToUniversalTime()) };
    }

    /// <summary>Reads what was appended since the last pass. Very long logs are read from their last few MB.</summary>
    private static void Read(Follow f)
    {
        try
        {
            using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < f.Offset) { f.Offset = 0; f.Pending = ""; }   // rewritten
            if (f.Offset == 0 && fs.Length > 4_000_000) f.Offset = fs.Length - 4_000_000;
            if (fs.Length == f.Offset) return;

            fs.Seek(f.Offset, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - f.Offset];
            int read = fs.Read(buffer, 0, buffer.Length);
            f.Offset += read;

            string text = f.Pending + Encoding.UTF8.GetString(buffer, 0, read);
            int lastBreak = text.LastIndexOf('\n');
            f.Pending = lastBreak < 0 ? text : text[(lastBreak + 1)..];
            if (lastBreak < 0) return;

            var now = DateTime.UtcNow;
            foreach (var line in text[..lastBreak].Split('\n'))
                f.State.Feed(line, now);
        }
        catch { /* rotated away or locked for a moment: try again next pass */ }
    }
}
