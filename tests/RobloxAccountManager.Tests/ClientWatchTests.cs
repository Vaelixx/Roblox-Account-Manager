using RobloxAccountManager.Services;
using Xunit;

namespace RobloxAccountManager.Tests;

public class ClientLogStateTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    // Shapes of the real lines (timestamp, thread, FLog channel, message).
    private const string Joining = "2026-10-05T12:00:05.100Z,5.1,1a2b,6 [FLog::Output] ! Joining game 'a1b2c3d4-0000-0000-0000-000000000000' place 920587237 at 10.0.0.1";
    private const string LoadTime = "2026-10-05T12:00:20.000Z,20.0,1a2b,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:920587237, visitid:0, publicIP:x, universeid:383310974, userid:123456, referral_page:x";
    private const string Joined = "2026-10-05T12:00:21.000Z,21.0,1a2b,6 [FLog::Network] Replicator created: 0x1234";
    private const string Disconnected = "2026-10-05T12:30:00.000Z,1800.0,1a2b,6 [FLog::Network] Time to disconnect replication data: 0.05";
    private const string Teleport = "2026-10-05T12:10:00.000Z,600.0,1a2b,6 [FLog::UgcExperienceController] UgcExperienceController: doTeleport: joinScriptUrl = https://x";
    private const string Leaving = "2026-10-05T12:31:00.000Z,1860.0,1a2b,6 [FLog::SingleSurfaceApp] leaveUGCGameInternal";

    private static ClientLogState InGame()
    {
        var s = new ClientLogState(T0);
        s.Feed(Joining, T0);
        s.Feed(LoadTime, T0);
        s.Feed(Joined, T0);
        return s;
    }

    [Fact]
    public void Join_reports_account_and_in_game()
    {
        var s = InGame();
        Assert.Equal(ClientPhase.InGame, s.Phase);
        Assert.Equal(123456, s.UserId);
        Assert.False(s.NeedsRecovery(T0.AddHours(5), Wait));
    }

    [Fact]
    public void Disconnect_needs_recovery_after_the_wait()
    {
        var s = InGame();
        s.Feed(Disconnected, T0.AddMinutes(30));
        Assert.Equal(ClientPhase.Out, s.Phase);
        Assert.False(s.NeedsRecovery(T0.AddMinutes(30.5), Wait));
        Assert.True(s.NeedsRecovery(T0.AddMinutes(31), Wait));
    }

    [Fact]
    public void Teleport_disconnect_is_expected()
    {
        var s = InGame();
        s.Feed(Teleport, T0.AddMinutes(10));
        s.Feed(Disconnected, T0.AddMinutes(10));
        Assert.Equal(ClientPhase.Teleporting, s.Phase);
        Assert.False(s.NeedsRecovery(T0.AddMinutes(11.5), Wait));

        s.Feed(Joining, T0.AddMinutes(10.2));
        s.Feed(Joined, T0.AddMinutes(10.5));
        Assert.Equal(ClientPhase.InGame, s.Phase);
    }

    [Fact]
    public void Teleport_that_never_lands_is_recovered()
    {
        var s = InGame();
        s.Feed(Teleport, T0.AddMinutes(10));
        Assert.True(s.NeedsRecovery(T0.AddMinutes(12), Wait));
    }

    [Fact]
    public void Leaving_to_the_home_screen_counts_as_out()
    {
        var s = InGame();
        s.Feed(Leaving, T0.AddMinutes(31));
        Assert.Equal(ClientPhase.Out, s.Phase);
    }

    [Fact]
    public void Slow_load_gets_eight_minutes()
    {
        var s = new ClientLogState(T0);
        Assert.False(s.NeedsRecovery(T0.AddMinutes(7), Wait));
        Assert.True(s.NeedsRecovery(T0.AddMinutes(8), Wait));
        s.Feed(Joining, T0.AddMinutes(1));
        Assert.False(s.NeedsRecovery(T0.AddMinutes(8.5), Wait));
    }

    [Theory]
    [InlineData("0.742.0.7421053_20261007T185308Z_Player_32D00_last.log", true)]
    [InlineData("0.742.0.7421053_20261007T185309Z_Player_88BE3_CrashHandler_last.log", false)]
    [InlineData("RobloxPlayerInstaller_37DD7.log", false)]
    public void Only_game_client_logs_are_matched(string name, bool expected)
        => Assert.Equal(expected, ClientLogFiles.IsClientLog(name));

    [Fact]
    public void A_log_known_to_be_another_accounts_is_skipped_for_the_next_closest()
    {
        var start = new DateTime(2026, 10, 7, 13, 53, 10);
        var logs = new[] { ("a.log", start.AddSeconds(1)), ("b.log", start.AddSeconds(4)) };
        var held = new HashSet<string>();
        var owners = new Dictionary<string, long> { ["a.log"] = 999 };

        Assert.Equal("a.log", ClientLogFiles.PickLog(logs, start, 999, held, owners));   // its own account
        Assert.Equal("b.log", ClientLogFiles.PickLog(logs, start, 111, held, owners));   // not the other account's
        held.Add("b.log");
        Assert.Null(ClientLogFiles.PickLog(logs, start, 111, held, owners));
    }

    [Theory]
    [InlineData("[FLog::Network] Disconnection Notification. Reason: 273", 273)]
    [InlineData("[FLog::Network] Sending disconnect with reason: 277", 277)]
    [InlineData("[FLog::Network] Lost connection with reason : Lost connection to the game server, please reconnect ID = 277", 277)]
    public void Disconnect_reason_codes_are_picked_up(string line, int code)
    {
        var s = InGame();
        s.Feed(line, T0.AddMinutes(30));
        Assert.Equal(code, s.Reason);
    }

    [Fact]
    public void A_new_join_clears_the_old_reason()
    {
        var s = InGame();
        s.Feed("[FLog::Network] Disconnection Notification. Reason: 273", T0.AddMinutes(30));
        s.Feed(Joining, T0.AddMinutes(31));
        Assert.Equal(0, s.Reason);
    }
}

public class RamGrowthTests
{
    [Fact]
    public void Baseline_is_taken_after_settling_and_needs_repeated_confirmation()
    {
        var g = new RamGrowth();
        Assert.False(g.Observe(TimeSpan.FromMinutes(2), 3000, 2));   // still loading: no baseline
        Assert.Equal(0, g.BaselineMb);
        Assert.False(g.Observe(TimeSpan.FromMinutes(11), 1000, 2));
        Assert.Equal(1000, g.BaselineMb);

        Assert.False(g.Observe(TimeSpan.FromMinutes(60), 2100, 2));
        Assert.False(g.Observe(TimeSpan.FromMinutes(61), 1500, 2));   // a spike, then back down
        for (int i = 1; i < RamGrowth.Confirmations; i++)
            Assert.False(g.Observe(TimeSpan.FromMinutes(70 + i), 2000, 2));
        Assert.True(g.Observe(TimeSpan.FromMinutes(80), 2050, 2));
    }
}

public class WindowLayoutTests
{
    private static readonly WindowLayout.Rect Area = new(0, 0, 1000, 600);

    [Fact]
    public void Fills_left_to_right_then_down()
    {
        Assert.Equal((0, 0), WindowLayout.FirstFreeCell(Area, 300, 200, Array.Empty<(int, int)>()));
        Assert.Equal((300, 0), WindowLayout.FirstFreeCell(Area, 300, 200, new[] { (0, 0) }));
        Assert.Equal((0, 200), WindowLayout.FirstFreeCell(Area, 300, 200, new[] { (0, 0), (300, 0), (600, 0) }));
    }

    [Fact]
    public void Reuses_a_freed_cell_and_wraps_when_full()
    {
        Assert.Equal((300, 0), WindowLayout.FirstFreeCell(Area, 300, 200, new[] { (0, 0), (600, 0) }));
        var all = new List<(int, int)>();
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) all.Add((x * 300, y * 200));
        Assert.Equal((0, 0), WindowLayout.FirstFreeCell(Area, 300, 200, all));
    }

    [Fact]
    public void Respects_the_work_area_offset()
    {
        Assert.Equal((100, 50), WindowLayout.FirstFreeCell(new WindowLayout.Rect(100, 50, 600, 400), 300, 200, Array.Empty<(int, int)>()));
    }
}

public class AfkTimingTests
{
    [Fact]
    public void Tap_timings_vary_within_bounds()
    {
        var rng = new Random(7);
        var seen = new HashSet<AfkSchedule.TapTiming>();
        for (int i = 0; i < 200; i++)
        {
            var t = AfkSchedule.RandomTiming(false, rng);
            Assert.InRange(t.SettleMs, 350, 900);
            Assert.InRange(t.HoldMs, 60, 200);
            Assert.InRange(t.AfterMs, 150, 600);
            Assert.InRange(t.GapMs, 400, 1500);
            seen.Add(t);
        }
        Assert.True(seen.Count > 150);
        Assert.InRange(AfkSchedule.RandomTiming(true, rng).SettleMs, 1350, 1900);
    }
}
