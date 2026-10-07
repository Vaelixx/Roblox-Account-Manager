using System.Text.Json;
using RobloxAccountManager.Models;
using Xunit;

namespace RobloxAccountManager.Tests;

public class LaunchPresetTests
{
    private const string Job = "0f8b2c1e-1234-4abc-9def-0123456789ab";

    private static LaunchPreset Legacy(long placeId, string jobId)
    {
        // Exactly what v2.0 wrote: no Destination, no link fields.
        string json = $$"""{"Name":"Farm","Aliases":["alt1"],"PlaceId":{{placeId}},"JobId":{{JsonSerializer.Serialize(jobId)}},"JoinDelaySeconds":8}""";
        return JsonSerializer.Deserialize<LaunchPreset>(json)!;
    }

    [Fact]
    public void Legacy_preset_without_server_stays_a_place()
    {
        var p = Legacy(123, "");
        Assert.Equal(JoinKind.Place, p.Destination);
        Assert.False(p.NormalizeDestination());
        Assert.Equal(JoinKind.Place, p.Destination);
        Assert.Equal(8, p.JoinDelaySeconds);
        Assert.Equal(0, p.RandomDelaySeconds);
        Assert.Equal("", p.PerformanceProfile);
    }

    [Fact]
    public void Legacy_job_id_becomes_a_server_destination()
    {
        var p = Legacy(123, Job);
        Assert.True(p.NormalizeDestination());
        Assert.Equal(JoinKind.Server, p.Destination);
        Assert.Equal(Job, p.JobId);
        Assert.False(p.NormalizeDestination());   // idempotent
    }

    [Fact]
    public void Private_link_typed_into_the_old_server_box_is_recovered()
    {
        const string link = "https://www.roblox.com/games/920587237/Adopt-Me?privateServerLinkCode=777";
        var p = Legacy(0, link);
        Assert.True(p.NormalizeDestination());
        Assert.Equal(JoinKind.PrivateServer, p.Destination);
        Assert.Equal(link, p.PrivateServerLink);
        Assert.Equal("", p.JobId);
        Assert.Equal(920587237, p.PlaceId);
    }

    [Fact]
    public void Share_link_in_the_old_server_box_keeps_the_place()
    {
        const string link = "https://www.roblox.com/share?code=abc&type=Server";
        var p = Legacy(55, link);
        Assert.True(p.NormalizeDestination());
        Assert.Equal(JoinKind.PrivateServer, p.Destination);
        Assert.Equal(55, p.PlaceId);
    }

    [Fact]
    public void Plain_game_link_in_the_old_server_box_is_just_the_place()
    {
        var p = Legacy(0, "https://www.roblox.com/games/606849621/Jailbreak");
        Assert.True(p.NormalizeDestination());
        Assert.Equal(JoinKind.Place, p.Destination);
        Assert.Equal(606849621, p.PlaceId);
        Assert.Equal("", p.JobId);
    }

    [Fact]
    public void Follow_preset_round_trips_and_names_its_destination()
    {
        var p = new LaunchPreset { Name = "Follow", Destination = JoinKind.FollowUser, FollowUserId = 42, FollowUsername = "builderman" };
        string json = JsonSerializer.Serialize(p);
        Assert.Contains("\"Destination\":\"FollowUser\"", json);
        var back = JsonSerializer.Deserialize<LaunchPreset>(json)!;
        Assert.Equal(JoinKind.FollowUser, back.Destination);
        Assert.Equal(42, back.FollowUserId);
        Assert.Equal("builderman", back.FollowUsername);
        Assert.False(back.NormalizeDestination());
    }

    [Fact]
    public void Private_server_link_is_never_written_in_clear_text()
    {
        var p = new LaunchPreset { Destination = JoinKind.PrivateServer, PrivateServerLink = "https://www.roblox.com/games/1/x?privateServerLinkCode=TOPSECRET123" };
        string json = JsonSerializer.Serialize(p);
        Assert.DoesNotContain("TOPSECRET123", json);
    }
}
