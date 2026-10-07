namespace RobloxAccountManager.Services;

/// <summary>What the disconnect watchdog does about a client after one presence reading.</summary>
public enum DisconnectVerdict
{
    /// <summary>Nothing for now: still loading, in a game, out of one for less than the limit, or dealt with already.</summary>
    Wait,

    /// <summary>The client is in a game: it loaded, so restarting this account works.</summary>
    Loaded,

    /// <summary>It was in a game and has not been for the limit: restart it.</summary>
    Disconnected,

    /// <summary>It never got into a game within the loading grace: restart it.</summary>
    NeverLoaded,

    /// <summary>
    /// It never got into a game, and neither did the clients the account's last restarts started: leave it
    /// running instead of restarting it forever.
    /// </summary>
    GiveUp,
}

/// <summary>
/// The disconnect watchdog's record of one client, fed its account's presence after every poll Roblox
/// answered. Kept free of WPF and Roblox calls so it can be tested.
///
/// A client counts as loaded once presence shows its account in a game, but not straight away when that is
/// still the server presence reported when the client was first seen (the rule the Ultra-low minimize uses):
/// presence lags behind, so it is most likely the session before this client. Once the loading grace is over
/// that session is long gone, and the same server means this client went back into it.
/// </summary>
public sealed class DisconnectTrack
{
    /// <summary>Restarts in a row whose new client never got into a game, after which the account is left alone.</summary>
    public const int MaxUnloadedRestarts = 3;

    /// <summary>Shortest time a client gets to load before it can count as never having got into a game.</summary>
    public static readonly TimeSpan MinLoadingGrace = TimeSpan.FromMinutes(5);

    // The server presence reported when the client was first seen, if it said In Game then ("" when the
    // server was hidden); null when it did not.
    private readonly string? _gameBefore;

    public DisconnectTrack(DateTime firstSeenUtc, bool inGame, string? gameId)
    {
        FirstSeenUtc = firstSeenUtc;
        _gameBefore = inGame ? gameId ?? "" : null;
    }

    public DateTime FirstSeenUtc { get; }

    /// <summary>Last reading that showed this client in a game; null while none has.</summary>
    public DateTime? LastInGameUtc { get; private set; }

    /// <summary>Set once the watchdog restarted or gave up on the client; cleared when it is in a game again.</summary>
    public bool Handled { get; set; }

    /// <summary>Records one presence reading and says what to do about the client.</summary>
    /// <param name="limit">How long a client may be out of a game (the disconnect setting).</param>
    /// <param name="accountSeenInGame">
    /// Presence has shown this account in a game during this session. Without that proof it may simply be
    /// hidden for the account (privacy settings), so a client that never seems to get in is left alone.
    /// </param>
    /// <param name="restartsSinceLoaded">The account's disconnect restarts since one of its clients was last in a game.</param>
    public DisconnectVerdict Observe(bool inGame, string? gameId, DateTime nowUtc, TimeSpan limit,
        bool accountSeenInGame, int restartsSinceLoaded)
    {
        var grace = limit > MinLoadingGrace ? limit : MinLoadingGrace;
        bool loading = nowUtc - FirstSeenUtc < grace;

        if (inGame)
        {
            if (loading && _gameBefore != null && (gameId ?? "") == _gameBefore) return DisconnectVerdict.Wait;
            LastInGameUtc = nowUtc;
            Handled = false;
            return DisconnectVerdict.Loaded;
        }

        if (Handled) return DisconnectVerdict.Wait;
        if (LastInGameUtc is { } last)
            return nowUtc - last >= limit ? DisconnectVerdict.Disconnected : DisconnectVerdict.Wait;

        // Never in a game yet: loading (and a slow presence update) gets the grace first.
        if (loading || !accountSeenInGame) return DisconnectVerdict.Wait;
        return restartsSinceLoaded >= MaxUnloadedRestarts ? DisconnectVerdict.GiveUp : DisconnectVerdict.NeverLoaded;
    }
}
