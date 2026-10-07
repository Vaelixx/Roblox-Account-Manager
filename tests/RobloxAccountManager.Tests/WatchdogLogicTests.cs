using RobloxAccountManager.Services;
using Xunit;

namespace RobloxAccountManager.Tests;

public class DisconnectTrackTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    private static DateTime At(double minutes) => T0.AddMinutes(minutes);

    [Fact]
    public void A_client_that_left_its_game_is_restarted_only_after_the_limit()
    {
        var track = new DisconnectTrack(T0, inGame: false, gameId: null);
        Assert.Equal(DisconnectVerdict.Loaded, track.Observe(true, "g1", At(1), Limit, true, 0));
        Assert.Equal(DisconnectVerdict.Wait, track.Observe(false, null, At(2), Limit, true, 0));
        Assert.Equal(DisconnectVerdict.Wait, track.Observe(false, null, At(3.9), Limit, true, 0));
        Assert.Equal(DisconnectVerdict.Disconnected, track.Observe(false, null, At(4), Limit, true, 0));
    }

    [Fact]
    public void Never_in_a_game_is_left_alone_while_presence_has_never_shown_the_account_in_one()
    {
        // Presence may be hidden for this account (privacy settings): no proof it can see it at all.
        var track = new DisconnectTrack(T0, inGame: false, gameId: null);
        Assert.Equal(DisconnectVerdict.Wait, track.Observe(false, null, At(30), Limit, accountSeenInGame: false, 0));
    }

    [Fact]
    public void Never_in_a_game_gets_the_loading_grace_first()
    {
        var track = new DisconnectTrack(T0, inGame: false, gameId: null);
        Assert.Equal(DisconnectVerdict.Wait, track.Observe(false, null, At(4.9), Limit, true, 0));
        Assert.Equal(DisconnectVerdict.NeverLoaded, track.Observe(false, null, At(5), Limit, true, 0));
    }

    [Fact]
    public void Gives_up_after_restarts_whose_clients_never_got_in()
    {
        var track = new DisconnectTrack(T0, inGame: false, gameId: null);
        Assert.Equal(DisconnectVerdict.GiveUp, track.Observe(false, null, At(10), Limit, true, DisconnectTrack.MaxUnloadedRestarts));
        Assert.Equal(DisconnectVerdict.NeverLoaded, track.Observe(false, null, At(10), Limit, true, DisconnectTrack.MaxUnloadedRestarts - 1));
    }

    [Fact]
    public void A_stale_in_game_from_the_previous_session_does_not_count_as_loaded()
    {
        // Presence still showed the old server when the relaunched client was first seen.
        var track = new DisconnectTrack(T0, inGame: true, gameId: "old-server");
        Assert.Equal(DisconnectVerdict.Wait, track.Observe(true, "old-server", At(1), Limit, true, 0));
        Assert.Null(track.LastInGameUtc);

        // A different server means this client got in.
        Assert.Equal(DisconnectVerdict.Loaded, track.Observe(true, "new-server", At(2), Limit, true, 0));
        Assert.Equal(At(2), track.LastInGameUtc);
    }

    [Fact]
    public void Handled_waits_until_the_client_is_back_in_a_game()
    {
        var track = new DisconnectTrack(T0, inGame: false, gameId: null);
        track.Observe(true, "g1", At(1), Limit, true, 0);
        Assert.Equal(DisconnectVerdict.Disconnected, track.Observe(false, null, At(5), Limit, true, 0));
        track.Handled = true;
        Assert.Equal(DisconnectVerdict.Wait, track.Observe(false, null, At(9), Limit, true, 0));
        Assert.Equal(DisconnectVerdict.Loaded, track.Observe(true, "g2", At(10), Limit, true, 0));
        Assert.False(track.Handled);
    }
}
