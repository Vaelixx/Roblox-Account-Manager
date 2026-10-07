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
    [InlineData("0f8b2c1e-1234-4abc-9def-0123456789ab", "0f8b2c1e-1234-4abc-9def-0123456789ab")]
    [InlineData(" \"0f8b2c1e-1234-4abc-9def-0123456789ab\", ", "0f8b2c1e-1234-4abc-9def-0123456789ab")]   // copied out of JSON or a list
    [InlineData("'0F8B2C1E-1234-4ABC-9DEF-0123456789AB';", "0F8B2C1E-1234-4ABC-9DEF-0123456789AB")]
    [InlineData("0f8b2c1e-1234-4abc-9def-0123456789ab\r\n", "0f8b2c1e-1234-4abc-9def-0123456789ab")]   // a whole line copied from a text file
    [InlineData("server 0f8b2c1e-1234-4abc-9def-0123456789ab", null)]   // a GUID inside other text is not an id
    [InlineData("0f8b2c1e-1234-4abc-9def-0123456789ab extra", null)]
    [InlineData("{0f8b2c1e-1234-4abc-9def-0123456789ab}", null)]
    [InlineData("0f8b2c1e12344abc9def0123456789ab", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeJobId(string? input, string? expected)
    {
        Assert.Equal(expected, JoinLinks.NormalizeJobId(input));
        Assert.Equal(expected != null, JoinLinks.LooksLikeJobId(input));
    }

    [Fact]
    public void Deep_link_with_a_malformed_instance_has_no_job()
    {
        var p = JoinLinks.Parse("roblox://experiences/start?placeId=606849621&gameInstanceId=abc0f8b2c1e-1234-4abc-9def-0123456789ab");
        Assert.Equal(606849621, p.PlaceId);
        Assert.Null(p.JobId);
    }

    [Theory]
    [InlineData("https://www.roblox.com/games/start?placeId=1&gameInstanceId=%220f8b2c1e-1234-4abc-9def-0123456789ab%22")]   // quoted, URL-encoded
    [InlineData("https://www.roblox.com/games/start?placeId=1&gameId=0f8b2c1e-1234-4abc-9def-0123456789ab")]
    [InlineData("https://www.roblox.com/games/start?placeId=1&jobId=0f8b2c1e-1234-4abc-9def-0123456789ab")]
    public void Job_id_from_a_link(string link)
        => Assert.Equal("0f8b2c1e-1234-4abc-9def-0123456789ab", JoinLinks.Parse(link).JobId);

    [Theory]
    [InlineData("https://www.roblox.com/games/1/x?linkCode=abc", "abc")]
    [InlineData("https://www.roblox.com/games/1/x?PRIVATESERVERLINKCODE=abc", "abc")]
    [InlineData("https://www.roblox.com/games/1/x?privateServerLinkCode=a%2Bb%3D", "a+b=")]   // decoded here, encoded again for the launch
    [InlineData("https://www.roblox.com/games/1/x?privateServerLinkCode=", null)]
    public void Private_server_code_from_a_link(string link, string? expected)
        => Assert.Equal(expected, JoinLinks.Parse(link).LinkCode);

    [Theory]
    [InlineData("https://www.roblox.com/share?type=Server&code=abc", "abc")]
    [InlineData("https://www.roblox.com/games/1818/x?code=abc", null)]   // a code is only a share code on a share link
    public void Share_code_from_a_link(string link, string? expected)
        => Assert.Equal(expected, JoinLinks.Parse(link).ShareCode);

    [Theory]
    [InlineData("https://www.roblox.com/games/1818/x?placeId=999", 1818)]   // the path wins over the query
    [InlineData("https://www.roblox.com/games/start?placeId=1818", 1818)]
    [InlineData("https://www.roblox.com/de/games/1818/Classic-Crossroads", 1818)]   // a localized page
    [InlineData("roblox.com/games/1818", 1818)]
    [InlineData("https://www.roblox.com/games/99999999999999999999/x", 0)]   // too big for an id
    public void Place_id_from_a_link(string link, long expected)
        => Assert.Equal(expected, JoinLinks.Parse(link).PlaceId);

    [Fact]
    public void Text_that_is_not_a_link_yields_nothing()
        => Assert.Equal(new JoinLinks.ParsedJoinLink(0, null, null, null), JoinLinks.Parse("not a link"));

    [Theory]
    [InlineData("920587237", 920587237)]
    [InlineData(" 920587237 ", 920587237)]
    [InlineData("https://www.roblox.com/games/920587237/Adopt-Me?privateServerLinkCode=555", 920587237)]   // not 920587237555
    [InlineData("https://www.roblox.com/share?code=abc&type=Server", 0)]
    [InlineData("", 0)]
    [InlineData("place 42", 42)]
    [InlineData("-5", 0)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("9223372036854775808", 0)]   // one past the largest id: no exception, no wrapped-around id
    [InlineData("0f8b2c1e-1234-4abc-9def-0123456789ab", 0)]   // a Job ID in the place box: its 20 digits are not a place
    [InlineData("https://www.roblox.com/games/start?placeId=-5", 0)]   // a link can't produce a negative place either
    public void ParsePlaceId(string input, long expected) => Assert.Equal(expected, JoinLinks.ParsePlaceId(input));
}
