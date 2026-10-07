using RobloxAccountManager.Services;
using Xunit;

namespace RobloxAccountManager.Tests;

public class RejoinBackoffTests
{
    [Fact]
    public void First_rejoins_are_immediate()
    {
        for (int streak = 0; streak < RejoinBackoff.FreeRejoins; streak++)
            Assert.True(RejoinBackoff.DelayFor(streak) < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Later_rejoins_slow_down_but_never_stop()
    {
        var previous = TimeSpan.Zero;
        for (int streak = RejoinBackoff.FreeRejoins; streak < 200; streak++)
        {
            var d = RejoinBackoff.DelayFor(streak);
            Assert.True(d >= previous);                          // never faster than before
            Assert.InRange(d, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));   // always another try, at most 15 min away
            previous = d;
        }
        Assert.Equal(TimeSpan.FromMinutes(15), RejoinBackoff.DelayFor(1000));
    }

    [Fact]
    public void A_client_that_ran_long_enough_resets_the_streak()
    {
        Assert.Equal(0, RejoinBackoff.StreakAfterExit(7, TimeSpan.FromMinutes(30)));
        Assert.Equal(7, RejoinBackoff.StreakAfterExit(7, TimeSpan.FromMinutes(2)));
    }
}

public class DataFolderMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ram-migration-" + Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(_root, "exe", "data");
    private string New => Path.Combine(_root, "appdata", "RobloxAccountManager", "data");

    public DataFolderMigrationTests()
    {
        Directory.CreateDirectory(Path.Combine(Old, "browser", "login-1"));
        File.WriteAllText(Path.Combine(Old, "accounts.dat"), "encrypted accounts");
        File.WriteAllText(Path.Combine(Old, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(Old, "cloakbrowser"));
        File.WriteAllText(Path.Combine(Old, "cloakbrowser", "chrome.exe"), "binary");
        File.WriteAllText(Path.Combine(Old, "browser", "login-1", "Cookies"), "temp profile");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Copies_everything_but_temp_profiles_and_leaves_the_old_folder_alone()
    {
        Assert.True(DataFolderMigration.CopyIfNeeded(Old, New));

        Assert.Equal("encrypted accounts", File.ReadAllText(Path.Combine(New, "accounts.dat")));
        Assert.True(File.Exists(Path.Combine(New, "settings.json")));
        Assert.True(File.Exists(Path.Combine(New, "cloakbrowser", "chrome.exe")));
        Assert.False(Directory.Exists(Path.Combine(New, "browser")));
        Assert.False(Directory.Exists(New + ".migrating"));

        // Nothing deleted from the old folder.
        Assert.True(File.Exists(Path.Combine(Old, "accounts.dat")));
        Assert.True(File.Exists(Path.Combine(Old, "browser", "login-1", "Cookies")));
    }

    [Fact]
    public void Never_overwrites_existing_data()
    {
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "accounts.dat"), "newer accounts");

        Assert.False(DataFolderMigration.CopyIfNeeded(Old, New));
        Assert.Equal("newer accounts", File.ReadAllText(Path.Combine(New, "accounts.dat")));
    }

    [Fact]
    public void Nothing_to_copy_is_a_no_op()
    {
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Assert.False(DataFolderMigration.CopyIfNeeded(empty, New));
        Assert.False(DataFolderMigration.CopyIfNeeded(Path.Combine(_root, "missing"), New));
        Assert.False(Directory.Exists(New));
    }
}
