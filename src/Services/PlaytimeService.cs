using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobloxAccountManager.Services;

/// <summary>One finished client session, as written to data/playtime.json.</summary>
public sealed class PlaySession
{
    /// <summary>Roblox user id the client was launched for.</summary>
    [JsonPropertyName("u")] public long UserId { get; set; }

    /// <summary>Session start, Unix seconds UTC.</summary>
    [JsonPropertyName("s")] public long StartUnix { get; set; }

    /// <summary>Session end, Unix seconds UTC.</summary>
    [JsonPropertyName("e")] public long EndUnix { get; set; }

    /// <summary>Place the session was launched into; 0 when unknown.</summary>
    [JsonPropertyName("p")] public long PlaceId { get; set; }

    [JsonIgnore] public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, EndUnix - StartUnix));
    [JsonIgnore] public DateTime EndLocal => DateTimeOffset.FromUnixTimeSeconds(EndUnix).LocalDateTime;
}

/// <summary>Per-account totals, recomputed whenever a session lands.</summary>
public sealed record PlaytimeSummary(TimeSpan Total, TimeSpan Last7Days, int Sessions, DateTime? LastPlayed)
{
    public static readonly PlaytimeSummary Empty = new(TimeSpan.Zero, TimeSpan.Zero, 0, null);
}

/// <summary>
/// Records how long each account's Roblox client actually ran.
///
/// <see cref="ProcessRegistry"/> already knows when a client started, but that knowledge died with
/// the process — nothing ever asked how long an account had been played for. Sessions are appended
/// to a small JSON file next to the settings so the answer survives restarts.
/// </summary>
public static class PlaytimeService
{
    private static readonly string FilePath = Paths.InData("playtime.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly object _gate = new();

    /// <summary>How long history is kept. Older sessions are dropped so the file stays small.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(180);

    /// <summary>
    /// Sessions shorter than this are discarded. A client that dies on startup — a bad cookie, a
    /// crash loop, a launch the user cancels — would otherwise fill the history with noise that
    /// says nothing about how much anyone actually played.
    /// </summary>
    private static readonly TimeSpan MinSession = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Nothing calls <see cref="ProcessRegistry.Prune"/> on its own unless the watchdog or the RAM
    /// monitor happens to be enabled, and Prune is what raises <see cref="ProcessRegistry.Exited"/>.
    /// This tick guarantees a closed client is noticed within a reasonable time either way.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(20);

    private static List<PlaySession> _sessions = new();
    private static Dictionary<long, PlaytimeSummary> _summaries = new();
    private static System.Threading.Timer? _sweep;
    private static bool _started;

    /// <summary>Raised after a session was recorded, so the UI can refresh its totals.</summary>
    public static event Action? Changed;

    /// <summary>Loads the history and starts watching for clients that exit.</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;

        Load();
        ProcessRegistry.Exited += OnClientExited;
        _sweep = new System.Threading.Timer(_ =>
        {
            try
            {
                ProcessRegistry.Prune();
                // Running clients count toward the totals as they play, so the numbers have to move
                // while they run, not only when one closes.
                if (SettingsService.Current.TrackPlaytime && ProcessRegistry.All.Any(t => !t.IsExternal && t.UserId > 0))
                    Changed?.Invoke();
            }
            catch { }
        }, null, SweepInterval, SweepInterval);
    }

    /// <summary>
    /// Books every still-running client as ending now. Called when the app closes: once it is
    /// gone nothing observes those clients any more, so the alternative is losing the session
    /// entirely — which for a manager left running all day is most of the recorded playtime.
    /// </summary>
    public static void FlushOpenSessions()
    {
        try
        {
            foreach (var t in ProcessRegistry.All) Record(t, save: false);
            Save();
        }
        catch (Exception ex) { Debug.WriteLine($"[Playtime] Flush failed: {ex.Message}"); }
    }

    private static void OnClientExited(ProcessRegistry.Tracked t) => Record(t, save: true);

    private static void Record(ProcessRegistry.Tracked t, bool save)
    {
        if (!SettingsService.Current.TrackPlaytime) return;
        if (t.UserId <= 0) return;                       // adopted external client — no account to credit
        if (t.Uptime < MinSession) return;

        var session = new PlaySession
        {
            UserId = t.UserId,
            StartUnix = new DateTimeOffset(DateTime.SpecifyKind(t.LaunchedUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            EndUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PlaceId = t.PlaceId,
        };

        lock (_gate)
        {
            // The sweep and the Exited event can both land on the same client; a session that is
            // already booked must not be counted twice.
            if (_sessions.Any(s => s.UserId == session.UserId && s.StartUnix == session.StartUnix)) return;
            _sessions.Add(session);
            Recompute();
        }

        if (save) Save();
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Totals for one account from finished sessions; never null.</summary>
    public static PlaytimeSummary For(long userId)
    {
        lock (_gate)
            return _summaries.TryGetValue(userId, out var s) ? s : PlaytimeSummary.Empty;
    }

    /// <summary>
    /// Time the account's running clients have been up so far. Sessions are only booked when a client
    /// closes, so without this a week of AFK in one long session showed nothing until it ended.
    /// Must not be called while holding <see cref="_gate"/>: reading the registry can book a session.
    /// </summary>
    private static Dictionary<long, TimeSpan> LiveByUser()
    {
        var live = new Dictionary<long, TimeSpan>();
        if (!SettingsService.Current.TrackPlaytime) return live;
        foreach (var t in ProcessRegistry.All)
            if (!t.IsExternal && t.UserId > 0 && t.Uptime >= MinSession)
                live[t.UserId] = live.GetValueOrDefault(t.UserId) + t.Uptime;
        return live;
    }

    /// <summary>Combined playtime across every account in the last seven days, running clients included.</summary>
    public static TimeSpan Last7DaysTotal
    {
        get
        {
            var live = LiveByUser().Values.Aggregate(TimeSpan.Zero, (a, t) => a + t);
            lock (_gate) return _summaries.Values.Aggregate(live, (a, s) => a + s.Last7Days);
        }
    }

    /// <summary>Combined playtime across every account, all time, running clients included.</summary>
    public static TimeSpan AllTimeTotal
    {
        get
        {
            var live = LiveByUser().Values.Aggregate(TimeSpan.Zero, (a, t) => a + t);
            lock (_gate) return _summaries.Values.Aggregate(live, (a, s) => a + s.Total);
        }
    }

    /// <summary>The most recent sessions, newest first — the Dashboard's recent-activity list.</summary>
    public static IReadOnlyList<PlaySession> Recent(int count)
    {
        lock (_gate)
            return _sessions.OrderByDescending(s => s.EndUnix).Take(count).ToList();
    }

    /// <summary>Drops the whole history. Returns false only when the file could not be removed.</summary>
    public static bool Clear()
    {
        lock (_gate)
        {
            _sessions = new List<PlaySession>();
            _summaries = new Dictionary<long, PlaytimeSummary>();
        }
        bool ok;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            ok = true;
        }
        catch { ok = false; }
        try { Changed?.Invoke(); } catch { }
        return ok;
    }

    /// <summary>
    /// Copies the current totals onto the account objects the lists bind to. The model holds
    /// pre-formatted strings so nothing in Models has to know this service exists.
    /// </summary>
    public static void Apply(IEnumerable<Models.Account> accounts)
    {
        var live = LiveByUser();
        foreach (var a in accounts)
        {
            var s = For(a.UserId);
            var running = live.GetValueOrDefault(a.UserId);
            a.PlaytimeTotal = s.Total + running;   // the "Playtime" sort reads this; it was never set before
            a.PlaytimeTotalText = Format(s.Total + running);
            a.Playtime7dText = Format(s.Last7Days + running);
            a.LastPlayedText = running > TimeSpan.Zero ? L.T("Playtime.PlayingNow")
                : s.LastPlayed is { } last
                    ? last.Date == DateTime.Today ? L.T("Time.TodayAt", last.ToString("t")) : last.ToString("g")
                    : L.T("Playtime.NeverPlayed");
            a.HasPlaytime = s.Total + running > TimeSpan.Zero;
        }
    }

    /// <summary>Human form used across the UI: "4h 12m", "38m", "—" for nothing.</summary>
    public static string Format(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "—";
        if (t.TotalHours >= 1) return L.T("Time.HoursMinutes", (int)t.TotalHours, t.Minutes);
        if (t.TotalMinutes >= 1) return L.T("Time.Minutes", (int)t.TotalMinutes);
        return L.T("Time.UnderMinute");
    }

    // ---- persistence ----

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) { lock (_gate) Recompute(); return; }
            var loaded = JsonSerializer.Deserialize<List<PlaySession>>(File.ReadAllText(FilePath));
            lock (_gate)
            {
                _sessions = loaded ?? new List<PlaySession>();
                Prune();
                Recompute();
            }
        }
        catch (Exception ex)
        {
            // History is a nicety, not data anyone can lose sleep over: start fresh rather than
            // block startup or overwrite a file someone may want to look at.
            Debug.WriteLine($"[Playtime] Could not read history: {ex.Message}");
            lock (_gate) { _sessions = new List<PlaySession>(); Recompute(); }
        }
    }

    private static void Save()
    {
        try
        {
            string json;
            lock (_gate) { Prune(); json = JsonSerializer.Serialize(_sessions, JsonOpts); }

            Directory.CreateDirectory(Paths.DataDir);
            // Same atomic staging as settings.json — a crash mid-write must not truncate the file.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex) { Debug.WriteLine($"[Playtime] Could not write history: {ex.Message}"); }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private static void Prune()
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(Retention).ToUnixTimeSeconds();
        _sessions.RemoveAll(s => s.EndUnix < cutoff);
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private static void Recompute()
    {
        long weekAgo = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
        var map = new Dictionary<long, PlaytimeSummary>();

        foreach (var group in _sessions.GroupBy(s => s.UserId))
        {
            var total = TimeSpan.Zero;
            var week = TimeSpan.Zero;
            long lastEnd = 0;

            foreach (var s in group)
            {
                total += s.Duration;
                if (s.EndUnix >= weekAgo) week += s.Duration;
                if (s.EndUnix > lastEnd) lastEnd = s.EndUnix;
            }

            map[group.Key] = new PlaytimeSummary(total, week, group.Count(),
                lastEnd > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastEnd).LocalDateTime : null);
        }

        _summaries = map;
    }
}
