using RobloxAccountManager.Services;
using Xunit;

namespace RobloxAccountManager.Tests;

public class AfkScheduleTests
{
    [Fact]
    public void Fixed_interval_is_exact()
    {
        var rng = new Random(1);
        Assert.Equal(TimeSpan.FromMinutes(15), AfkSchedule.NextInterval(15, 20, randomize: false, rng));
        Assert.Equal(TimeSpan.FromMinutes(8), AfkSchedule.NextInterval(8, 8, randomize: true, rng));
    }

    [Fact]
    public void Random_interval_stays_within_bounds_and_varies()
    {
        var rng = new Random(7);
        var seen = new HashSet<double>();
        for (int i = 0; i < 500; i++)
        {
            var t = AfkSchedule.NextInterval(8, 14, randomize: true, rng);
            Assert.InRange(t, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(14));
            seen.Add(t.TotalSeconds);
        }
        Assert.True(seen.Count > 50);
    }

    [Fact]
    public void Bad_bounds_are_repaired()
    {
        var rng = new Random(3);
        Assert.Equal(TimeSpan.FromMinutes(1), AfkSchedule.NextInterval(0, 0, randomize: true, rng));
        Assert.Equal(TimeSpan.FromMinutes(10), AfkSchedule.NextInterval(10, 5, randomize: true, rng));   // max below min
    }

    [Fact]
    public void First_delay_is_never_later_than_a_full_interval()
    {
        var rng = new Random(11);
        for (int i = 0; i < 500; i++)
        {
            var d = AfkSchedule.FirstDelay(10, 10, randomize: false, rng);
            Assert.InRange(d, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        }
    }

    [Fact]
    public void Failures_retry_quickly_then_back_off()
    {
        var normal = TimeSpan.FromMinutes(12);
        Assert.Equal(TimeSpan.FromMinutes(1), AfkSchedule.AfterFailure(1, normal));
        Assert.Equal(TimeSpan.FromMinutes(1), AfkSchedule.AfterFailure(3, normal));
        Assert.Equal(normal, AfkSchedule.AfterFailure(4, normal));
    }
}

public class BrowserLivenessTests
{
    [Fact]
    public void Launcher_handoff_does_not_look_like_a_closed_window()
    {
        // The endpoint answers and lists the sign-in page: open, however often it is asked.
        var l = new BrowserLiveness();
        for (int i = 0; i < 20; i++) Assert.False(l.Observe(true, 1));
    }

    [Fact]
    public void Closed_only_after_consecutive_misses()
    {
        var l = new BrowserLiveness();
        Assert.False(l.Observe(true, 1));
        Assert.False(l.Observe(false, 0));
        Assert.False(l.Observe(false, 0));
        Assert.False(l.Observe(true, 1));    // one slow answer resets the count
        Assert.False(l.Observe(false, 0));
        Assert.False(l.Observe(true, 0));
        Assert.True(l.Observe(false, 0));
    }

    [Fact]
    public void Background_browser_without_windows_counts_as_closed()
    {
        var l = new BrowserLiveness();
        l.Observe(true, 1);
        Assert.False(l.Observe(true, 0));
        Assert.False(l.Observe(true, 0));
        Assert.True(l.Observe(true, 0));
    }

    [Fact]
    public void No_pages_before_any_page_was_seen_is_not_a_close()
    {
        var l = new BrowserLiveness();
        for (int i = 0; i < 10; i++) Assert.False(l.Observe(true, 0));
        Assert.False(l.Observe(false, 0));
        Assert.False(l.Observe(false, 0));
        Assert.True(l.Observe(false, 0));    // the endpoint itself going away still counts
    }
}

public class PerformanceProfileTests
{
    [Fact]
    public void Normal_profile_changes_nothing()
    {
        var defaults = new UltraLowOptions();
        Assert.Empty(PerformanceProfiles.Flags(PerformanceProfiles.Normal, defaults));
        Assert.Equal(0, PerformanceProfiles.FpsCap(PerformanceProfiles.Normal, defaults));
        Assert.False(PerformanceProfiles.Minimizes(PerformanceProfiles.Normal, defaults));
        Assert.True(PerformanceProfiles.IsKnown(""));
        Assert.False(PerformanceProfiles.IsKnown("Turbo"));
    }

    [Fact]
    public void Ultra_low_uses_only_known_allowlisted_flags()
    {
        // Mirror of FFlagsService.Allowlist entries the profile relies on.
        var allowlisted = new HashSet<string>
        {
            "DFIntDebugFRMQualityLevelOverride", "FIntDebugForceMSAASamples", "DFFlagTextureQualityOverrideEnabled",
            "DFIntTextureQualityOverride", "FIntFRMMinGrassDistance", "FIntFRMMaxGrassDistance",
            "FFlagDebugSkyGray", "DFFlagDebugPauseVoxelizer",
        };
        var flags = PerformanceProfiles.Flags(PerformanceProfiles.UltraLowAfk, new UltraLowOptions());
        Assert.Equal(allowlisted.Count, flags.Count);
        Assert.All(flags.Keys, k => Assert.Contains(k, allowlisted));
        Assert.Equal(15, PerformanceProfiles.FpsCap(PerformanceProfiles.UltraLowAfk, new UltraLowOptions()));
        Assert.Equal(5, PerformanceProfiles.FpsCap(PerformanceProfiles.UltraLowAfk, new UltraLowOptions { FpsCap = 2 }));
    }

    [Fact]
    public void Ultra_low_parts_can_be_switched_off_one_by_one()
    {
        var none = new UltraLowOptions
        {
            LowestQuality = false, NoAntiAliasing = false, LowestTextures = false, NoGrass = false,
            GraySky = false, FreezeLighting = false, FpsCap = 0, MinimizeWhenInGame = false,
        };
        Assert.Empty(PerformanceProfiles.Flags(PerformanceProfiles.UltraLowAfk, none));
        Assert.Equal(0, PerformanceProfiles.FpsCap(PerformanceProfiles.UltraLowAfk, none));
        Assert.False(PerformanceProfiles.Minimizes(PerformanceProfiles.UltraLowAfk, none));

        var onlySky = new UltraLowOptions { LowestQuality = false, NoAntiAliasing = false, LowestTextures = false, NoGrass = false, FreezeLighting = false };
        Assert.Equal(new[] { "FFlagDebugSkyGray" }, PerformanceProfiles.Flags(PerformanceProfiles.UltraLowAfk, onlySky).Keys);
    }

    [Fact]
    public void Undo_restores_previous_values_and_removes_added_flags()
    {
        var written = new Dictionary<string, string> { ["A"] = "1", ["B"] = "True", ["C"] = "0" };
        var previous = new Dictionary<string, string> { ["A"] = "4" };                   // B and C did not exist
        var thisLaunch = new Dictionary<string, string> { ["C"] = "2" };                // normal settings set C
        var inFile = new Dictionary<string, string> { ["A"] = "1", ["B"] = "True", ["C"] = "0", ["X"] = "keep" };

        var edits = PerformanceProfiles.UndoEdits(written, previous, thisLaunch, inFile);

        Assert.Equal("4", edits["A"]);          // restored
        Assert.Null(edits["B"]);                // removed
        Assert.False(edits.ContainsKey("C"));   // this launch writes C itself
        Assert.False(edits.ContainsKey("X"));   // never touched by the profile
    }

    [Fact]
    public void Undo_leaves_flags_the_user_changed_since()
    {
        var written = new Dictionary<string, string> { ["A"] = "1" };
        var edits = PerformanceProfiles.UndoEdits(written, new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, string> { ["A"] = "8" });
        Assert.Empty(edits);
    }
}

public class RedactionTests
{
    [Theory]
    [InlineData("cookie=_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you|_ABCDEF0123", "ABCDEF0123")]
    [InlineData(".ROBLOSECURITY=abcdef0123456789; path=/", "abcdef0123456789")]
    [InlineData("https://www.roblox.com/games/1/x?privateServerLinkCode=998877&foo=1", "998877")]
    [InlineData("placelauncherurl?request=RequestPrivateGame&accessCode=aaaa-bbbb&linkCode=1234", "aaaa-bbbb")]
    [InlineData("https://www.roblox.com/share?code=sharecode42&type=Server", "sharecode42")]
    [InlineData("posting to https://discord.com/api/webhooks/123456/tok-en_value failed", "tok-en_value")]
    [InlineData("Authorization: Bearer s3cr3tT0ken", "s3cr3tT0ken")]
    [InlineData("GET /launch?account=a&token=hunter2", "hunter2")]
    [InlineData("proxy http://bob:pa55word@10.0.0.1:8080 refused", "pa55word")]
    public void Secrets_are_removed(string input, string secret)
    {
        Assert.DoesNotContain(secret, Redaction.Apply(input));
    }

    [Fact]
    public void Ordinary_text_is_untouched()
    {
        const string line = "2026-09-25 10:00:00Z  [INFO] [launcher] Launched Alt1 into place 606849621";
        Assert.Equal(line, Redaction.Apply(line));
    }
}
