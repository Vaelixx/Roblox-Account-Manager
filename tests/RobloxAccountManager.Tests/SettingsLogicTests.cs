using System.Text;
using System.Text.Json;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;
using Xunit;

namespace RobloxAccountManager.Tests;

public class PresetDestinationTests
{
    private const string Job = "0f8b2c1e-1234-4abc-9def-0123456789ab";

    [Fact]
    public void A_place_preset_that_kept_an_old_job_id_stays_a_place()
    {
        // Saved after the user switched a Server preset back to Place: the Job ID was left behind.
        string json = $$"""{"Name":"Farm","Destination":"Place","PlaceId":5,"JobId":"{{Job}}"}""";
        var p = JsonSerializer.Deserialize<LaunchPreset>(json)!;

        Assert.False(p.NormalizeDestination());
        Assert.Equal(JoinKind.Place, p.Destination);
    }

    [Fact]
    public void A_preset_written_by_this_version_reads_back_unchanged()
    {
        var saved = new LaunchPreset { Name = "Farm", PlaceId = 5, Destination = JoinKind.Server, JobId = Job };
        var p = JsonSerializer.Deserialize<LaunchPreset>(JsonSerializer.Serialize(saved))!;

        Assert.False(p.NormalizeDestination());
        Assert.Equal(JoinKind.Server, p.Destination);
        Assert.Equal(Job, p.JobId);
    }

    [Fact]
    public void A_preset_from_before_destinations_is_still_upgraded_once()
    {
        string json = $$"""{"Name":"Farm","PlaceId":5,"JobId":"{{Job}}"}""";
        var p = JsonSerializer.Deserialize<LaunchPreset>(json)!;

        Assert.True(p.NormalizeDestination());
        Assert.Equal(JoinKind.Server, p.Destination);
        Assert.False(p.NormalizeDestination());
    }
}

public class RedactionMoreTests
{
    [Fact]
    public void A_roblox_player_link_keeps_none_of_its_secrets()
    {
        const string uri = "roblox-player:1+launchmode:play+gameinfo:TICKETSECRET123+launchtime:1"
            + "+placelauncherurl:https%3a%2f%2fassetgame.roblox.com%2fgame%2fPlaceLauncher.ashx%3frequest%3dRequestPrivateGame"
            + "%26placeId%3d5%26accessCode%3dACCESSSECRET-1%26linkCode%3d987654321%26isPlayTogetherGame%3dfalse+browsertrackerid:1";

        string clean = Redaction.Apply(uri);

        Assert.DoesNotContain("TICKETSECRET123", clean);
        Assert.DoesNotContain("ACCESSSECRET-1", clean);
        Assert.DoesNotContain("987654321", clean);
        Assert.Contains("isPlayTogetherGame", clean);   // only the secrets go, not the rest of the link
    }

    [Theory]
    [InlineData("""{"privateServerInviteData":{"placeId":5,"linkCode":"LINKSECRET"}}""", "LINKSECRET")]
    [InlineData("""{"linkId":"SHARESECRET","linkType":"Server"}""", "SHARESECRET")]
    [InlineData("roblox://navigation/share_links?code=SHARESECRET&type=Server", "SHARESECRET")]
    [InlineData("https://www.roblox.com/share?code=SHARESECRET&type=Server", "SHARESECRET")]
    public void Codes_in_json_and_app_links_are_removed(string input, string secret)
        => Assert.DoesNotContain(secret, Redaction.Apply(input));

    [Fact]
    public void A_large_report_is_redacted_in_time_instead_of_coming_out_empty()
    {
        var sb = new StringBuilder();
        string run = new('a', 1900);
        for (int i = 0; i < 150; i++) sb.Append("12:00:00 INFO  test  ").AppendLine(run);
        sb.AppendLine("proxy http://bob:pa55word@10.0.0.1:8080 refused");

        string clean = Redaction.Apply(sb.ToString());

        Assert.NotEqual(Redaction.Marker, clean);
        Assert.DoesNotContain("pa55word", clean);
        Assert.Contains("http://" + Redaction.Marker + "@10.0.0.1", clean);
    }

    [Fact]
    public void Redacting_twice_changes_nothing_more()
    {
        const string text = "cookie _|WARNING:-DO-NOT-SHARE-THIS.--abc and https://x.y/games/1?privateServerLinkCode=42 and Bearer abc.def";
        string once = Redaction.Apply(text);
        Assert.Equal(once, Redaction.Apply(once));
        Assert.Equal("", Redaction.Apply(null));
    }
}

public class DataFolderChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ram-choice-" + Guid.NewGuid().ToString("N"));
    private string Exe => Path.Combine(_root, "exe", "data");
    private string PerUser => Path.Combine(_root, "appdata", "RobloxAccountManager", "data");
    private string Marker => Path.Combine(PerUser, DataFolderMigration.MovedMarker);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private void SeedExeFolder()
    {
        Directory.CreateDirectory(Exe);
        File.WriteAllText(Path.Combine(Exe, "accounts.dat"), "encrypted accounts");
    }

    [Fact]
    public void A_writable_folder_next_to_the_exe_is_used()
    {
        SeedExeFolder();
        Assert.Equal(Exe, DataFolderMigration.Choose(Exe, PerUser, _ => true));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void A_move_is_remembered_even_when_the_exe_folder_becomes_writable_again()
    {
        SeedExeFolder();
        Assert.Equal(PerUser, DataFolderMigration.Choose(Exe, PerUser, _ => false));
        Assert.Equal("encrypted accounts", File.ReadAllText(Path.Combine(PerUser, "accounts.dat")));
        Assert.True(File.Exists(Marker));

        // Next start: the probe would succeed now, but the data has lived in the per-user folder since.
        Assert.Equal(PerUser, DataFolderMigration.Choose(Exe, PerUser, _ => true));
    }

    [Fact]
    public void Existing_per_user_data_is_used_but_not_made_permanent()
    {
        SeedExeFolder();
        Directory.CreateDirectory(PerUser);
        File.WriteAllText(Path.Combine(PerUser, "accounts.dat"), "other accounts");

        Assert.Equal(PerUser, DataFolderMigration.Choose(Exe, PerUser, _ => false));
        Assert.Equal("other accounts", File.ReadAllText(Path.Combine(PerUser, "accounts.dat")));
        Assert.False(File.Exists(Marker));
        Assert.Equal(Exe, DataFolderMigration.Choose(Exe, PerUser, _ => true));
    }

    [Fact]
    public void Only_temporary_browser_files_need_no_copy_and_keep_the_target()
    {
        Directory.CreateDirectory(Path.Combine(Exe, "browser", "login-1"));
        File.WriteAllText(Path.Combine(Exe, "browser", "login-1", "Cookies"), "temp profile");
        Directory.CreateDirectory(PerUser);

        Assert.False(DataFolderMigration.CopyIfNeeded(Exe, PerUser));
        Assert.True(Directory.Exists(PerUser));
        Assert.False(Directory.Exists(PerUser + ".migrating"));
    }

    [Fact]
    public void Copies_lose_the_read_only_flag_and_skip_the_private_browser()
    {
        SeedExeFolder();
        File.SetAttributes(Path.Combine(Exe, "accounts.dat"), FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(Exe, "cloakbrowser", "win"));
        File.WriteAllText(Path.Combine(Exe, "cloakbrowser", "win", "chrome.exe"), "binary");

        Assert.True(DataFolderMigration.CopyIfNeeded(Exe, PerUser));
        Assert.False(File.GetAttributes(Path.Combine(PerUser, "accounts.dat")).HasFlag(FileAttributes.ReadOnly));
        Assert.False(Directory.Exists(Path.Combine(PerUser, "cloakbrowser")));
    }

    [Fact]
    public void Write_probe_leaves_nothing_behind()
    {
        Directory.CreateDirectory(Exe);
        Assert.True(DataFolderMigration.CanWrite(Exe));
        Assert.Empty(Directory.EnumerateFiles(Exe, ".wtest*"));
    }
}
