using RobloxAccountManager.Models;
using Xunit;

namespace RobloxAccountManager.Tests;

public class JoinTargetTests
{
    private const string Job = "0f8b2c1e-1234-4abc-9def-0123456789ab";

    [Fact]
    public void Kind_follows_the_richest_field()
    {
        Assert.Equal(JoinKind.Place, new JoinTarget(1).Kind);
        Assert.Equal(JoinKind.Server, new JoinTarget(1, JobId: Job).Kind);
        Assert.Equal(JoinKind.PrivateServer, new JoinTarget(1, LinkCode: "abc").Kind);
        Assert.Equal(JoinKind.PrivateServer, new JoinTarget(1, AccessCode: "abc").Kind);
        Assert.Equal(JoinKind.FollowUser, new JoinTarget(0, FollowUserId: 42).Kind);
    }

    [Fact]
    public void ToString_never_contains_private_server_codes()
    {
        var t = new JoinTarget(123, LinkCode: "SECRETLINK", AccessCode: "SECRETACCESS");
        string text = t.ToString();
        Assert.DoesNotContain("SECRETLINK", text);
        Assert.DoesNotContain("SECRETACCESS", text);
        Assert.Contains("123", text);
        Assert.DoesNotContain("SECRET", $"{t}");   // interpolation goes through ToString too
    }

    [Fact]
    public void Rejoin_keeps_private_servers_and_followed_players()
    {
        var priv = new JoinTarget(5, LinkCode: "code");
        var follow = new JoinTarget(0, FollowUserId: 9);
        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(priv, priv.ForRejoin(i));
            Assert.Equal(follow, follow.ForRejoin(i));
        }
    }

    [Fact]
    public void Rejoin_drops_a_public_server_only_after_the_first_attempt()
    {
        var server = new JoinTarget(5, JobId: Job);
        Assert.Equal(server, server.ForRejoin(1));
        Assert.Equal(new JoinTarget(5), server.ForRejoin(2));
        Assert.Equal(new JoinTarget(5), server.ForRejoin(3));
    }
}

public class JoinLinksTests
{
    [Fact]
    public void Classic_private_server_link()
    {
        var p = JoinLinks.Parse("https://www.roblox.com/games/920587237/Adopt-Me?privateServerLinkCode=12345678901234567890");
        Assert.Equal(920587237, p.PlaceId);
        Assert.Equal("12345678901234567890", p.LinkCode);
        Assert.Null(p.ShareCode);
    }

    [Fact]
    public void Share_link_without_scheme()
    {
        var p = JoinLinks.Parse("www.roblox.com/share?code=abcdef123&type=Server");
        Assert.Equal("abcdef123", p.ShareCode);
        Assert.Equal(0, p.PlaceId);
    }

    [Fact]
    public void Deep_link_with_instance()
    {
        var p = JoinLinks.Parse("roblox://experiences/start?placeId=606849621&gameInstanceId=0f8b2c1e-1234-4abc-9def-0123456789ab");
        Assert.Equal(606849621, p.PlaceId);
        Assert.Equal("0f8b2c1e-1234-4abc-9def-0123456789ab", p.JobId);
    }

    [Theory]
    [InlineData("920587237", 920587237)]
    [InlineData(" 920587237 ", 920587237)]
    [InlineData("https://www.roblox.com/games/920587237/Adopt-Me?privateServerLinkCode=555", 920587237)]   // not 920587237555
    [InlineData("https://www.roblox.com/share?code=abc&type=Server", 0)]
    [InlineData("", 0)]
    [InlineData("place 42", 42)]
    public void ParsePlaceId(string input, long expected) => Assert.Equal(expected, JoinLinks.ParsePlaceId(input));
}
