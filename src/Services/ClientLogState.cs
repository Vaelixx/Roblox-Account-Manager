using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services;

/// <summary>Where a client is, as far as its own log says.</summary>
public enum ClientPhase
{
    /// <summary>Started; no join seen yet.</summary>
    Starting,
    /// <summary>Joining a server (or reconnecting after a teleport).</summary>
    Joining,
    InGame,
    /// <summary>A teleport is under way: the coming disconnect is expected.</summary>
    Teleporting,
    /// <summary>Disconnected from its server, or back on the home screen.</summary>
    Out,
}

/// <summary>
/// Reads one Roblox client's log (%LOCALAPPDATA%\Roblox\logs) line by line and tracks whether that
/// client is in a game. This is the client's own account of a disconnect — kick, server shutdown,
/// network drop, "joined from another device" — which presence can't give: presence lags, and an
/// account that hides its game from others never shows as in game at all.
///
/// The marker lines are the ones Bloxstrap's activity watcher relies on. Reason codes are not
/// reliably logged in one format, so they are picked up loosely and only used where noted.
/// </summary>
public sealed class ClientLogState
{
    private const string JoiningEntry = "! Joining game";
    private const string JoinedEntry = "[FLog::Network] Replicator created";
    private const string DisconnectedEntry = "[FLog::Network] Time to disconnect replication data";
    private const string TeleportEntry = "doTeleport";
    private const string LeavingEntry = "leaveUGCGameInternal";

    private static readonly Regex UserIdPattern = new(@"userid:(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "... disconnect ... reason: 277", "Disconnection Notification. Reason: 273", "... ID = 277"
    private static readonly Regex ReasonPattern = new(@"(?:disconnect|lost connection).*?(?:reason\D{0,4}|ID\s*=\s*)(\d{3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Error 273: the same account joined a game from somewhere else.</summary>
    public const int ReasonJoinedElsewhere = 273;

    public ClientPhase Phase { get; private set; } = ClientPhase.Starting;
    public DateTime PhaseSinceUtc { get; private set; }

    /// <summary>Account the log belongs to, once a join has reported it (0 until then).</summary>
    public long UserId { get; private set; }

    /// <summary>Last disconnect reason code seen since the last join, or 0.</summary>
    public int Reason { get; private set; }

    /// <summary>The line that ended the last game session, for the diagnostics log.</summary>
    public string? LastDisconnectLine { get; private set; }

    /// <summary>True once this client has been in a game at least once.</summary>
    public bool EverInGame { get; private set; }

    public ClientLogState(DateTime startedUtc) => PhaseSinceUtc = startedUtc;

    public void Feed(string line, DateTime nowUtc)
    {
        if (UserId == 0)
        {
            var m = UserIdPattern.Match(line);
            if (m.Success && long.TryParse(m.Groups[1].Value, out long id)) UserId = id;
        }

        if (line.Contains(JoiningEntry, StringComparison.Ordinal))
        {
            Reason = 0;
            Set(ClientPhase.Joining, nowUtc);
        }
        else if (line.Contains(JoinedEntry, StringComparison.Ordinal))
        {
            EverInGame = true;
            Set(ClientPhase.InGame, nowUtc);
        }
        else if (line.Contains(TeleportEntry, StringComparison.Ordinal))
        {
            Set(ClientPhase.Teleporting, nowUtc);
        }
        else if (line.Contains(DisconnectedEntry, StringComparison.Ordinal))
        {
            // Part of a teleport: the next join follows on its own.
            if (Phase == ClientPhase.Teleporting) return;
            LastDisconnectLine = line;
            Set(ClientPhase.Out, nowUtc);
        }
        else if (line.Contains(LeavingEntry, StringComparison.Ordinal))
        {
            LastDisconnectLine ??= line;
            Set(ClientPhase.Out, nowUtc);
        }

        if (line.Contains("isconnect", StringComparison.Ordinal) || line.Contains("ost connection", StringComparison.Ordinal))
        {
            var m = ReasonPattern.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int code))
            {
                Reason = code;
                LastDisconnectLine = line;
            }
        }
    }

    private void Set(ClientPhase phase, DateTime nowUtc)
    {
        if (Phase == phase) return;
        Phase = phase;
        PhaseSinceUtc = nowUtc;
    }

    /// <summary>
    /// True when the client has been out of a game long enough to restart it: <paramref name="wait"/>
    /// after a disconnect, longer while a teleport or a join is still allowed to finish.
    /// </summary>
    public bool NeedsRecovery(DateTime nowUtc, TimeSpan wait)
    {
        var grace = Phase switch
        {
            ClientPhase.InGame => TimeSpan.MaxValue,
            ClientPhase.Out => wait,
            ClientPhase.Teleporting => Max(wait, TimeSpan.FromMinutes(2)),
            _ => Max(wait, TimeSpan.FromMinutes(8)),   // starting or joining: a slow load (six clients, after an update) is not a disconnect
        };
        return grace != TimeSpan.MaxValue && nowUtc - PhaseSinceUtc >= grace;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
