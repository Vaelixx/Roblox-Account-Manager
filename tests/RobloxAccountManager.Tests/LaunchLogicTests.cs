using RobloxAccountManager.Services;
using Xunit;

namespace RobloxAccountManager.Tests;

public class LaunchBarInputTests
{
    private const string Job = "0f8b2c1e-1234-4abc-9def-0123456789ab";
    private const string PrivateLink = "https://www.roblox.com/games/920587237/Adopt-Me?privateServerLinkCode=12345678901234567890";
    private const string ShareLink = "https://www.roblox.com/share?code=abcdef123&type=Server";
    private const string GameLink = "https://www.roblox.com/games/920587237/Adopt-Me";

    [Theory]
    [InlineData(PrivateLink, true)]
    [InlineData(ShareLink, true)]
    [InlineData(GameLink, false)]
    [InlineData(Job, false)]
    [InlineData("roblox://experiences/start?placeId=606849621&gameInstanceId=" + Job, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_links_that_open_a_private_server_are_hidden(string? text, bool expected)
        => Assert.Equal(expected, LaunchBarInput.IsPrivateLink(text));

    [Theory]
    [InlineData(PrivateLink, true)]
    [InlineData(ShareLink, true)]
    [InlineData(GameLink, true)]
    [InlineData(Job, false)]                                                                   // a public server is gone by the next start
    [InlineData("roblox://experiences/start?placeId=606849621&gameInstanceId=" + Job, false)]   // a link that names one, too
    [InlineData("not a link", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_lasting_destinations_are_remembered(string? text, bool expected)
        => Assert.Equal(expected, LaunchBarInput.IsWorthRemembering(text));
}
