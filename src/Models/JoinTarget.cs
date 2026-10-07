using System.Text.RegularExpressions;
using System.Web;

namespace RobloxAccountManager.Models;

/// <summary>What kind of destination a <see cref="JoinTarget"/> is.</summary>
public enum JoinKind { Place, Server, PrivateServer, FollowUser }

/// <summary>
/// Everything that identifies where a launch should land. The one destination type every launch
/// path uses — the launch bar, presets, schedules, the crash watchdog, the local API and plugins —
/// so a private server or a followed player is never reduced to "place + job" somewhere along the way.
/// </summary>
/// <remarks>
/// <see cref="LinkCode"/> and <see cref="AccessCode"/> are credentials: anyone holding them can join
/// that private server. <see cref="ToString"/> is overridden so a target that ends up in a log line or
/// an exception message never carries them (the compiler-generated record ToString prints every member).
/// </remarks>
public sealed record JoinTarget(long PlaceId, string? JobId = null, long FollowUserId = 0, string? LinkCode = null, string? AccessCode = null)
{
    public JoinKind Kind =>
        FollowUserId > 0 ? JoinKind.FollowUser
        : !string.IsNullOrEmpty(LinkCode) || !string.IsNullOrEmpty(AccessCode) ? JoinKind.PrivateServer
        : !string.IsNullOrEmpty(JobId) ? JoinKind.Server
        : JoinKind.Place;

    /// <summary>The same place without the specific public server — the fallback when that server is gone.</summary>
    public JoinTarget WithoutServer() => this with { JobId = null };

    /// <summary>
    /// The destination for the <paramref name="rejoinInWindow"/>-th crash rejoin in a row. A specific
    /// public server the client already dropped out of once is most likely gone (shut down, full), so
    /// from the second rejoin on it becomes the same place. Private servers and followed players are
    /// kept: there is no other server they could mean.
    /// </summary>
    public JoinTarget ForRejoin(int rejoinInWindow)
        => Kind == JoinKind.Server && rejoinInWindow >= 2 ? WithoutServer() : this;

    /// <summary>Log-safe description; never includes private-server codes.</summary>
    public override string ToString() => Kind switch
    {
        JoinKind.FollowUser => $"follow user {FollowUserId}",
        JoinKind.PrivateServer => $"private server in place {PlaceId}",
        JoinKind.Server => $"server {JobId} in place {PlaceId}",
        _ => $"place {PlaceId}",
    };
}

/// <summary>Pure (network-free) parsing of what people paste into a Place / Server / link box.</summary>
public static class JoinLinks
{
    public record ParsedJoinLink(long PlaceId, string? LinkCode, string? ShareCode, string? JobId);

    /// <summary>
    /// A Job ID as Roblox uses it: exactly one GUID. Quotes, commas and spaces that come along when an id
    /// is copied out of a list or a JSON snippet are dropped; anything else around the GUID is refused
    /// rather than sent to Roblox as part of the id. Null when the text is not a Job ID.
    /// </summary>
    public static string? NormalizeJobId(string? text)
    {
        string t = (text ?? "").Trim().Trim('"', '\'', ',', ';', ' ');
        return Guid.TryParseExact(t, "D", out _) ? t : null;
    }

    public static bool LooksLikeJobId(string? text) => NormalizeJobId(text) != null;

    /// <summary>True when the text is a link rather than a bare id.</summary>
    public static bool LooksLikeLink(string? text)
    {
        string t = (text ?? "").Trim();
        return t.Contains("roblox.com", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("http", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls what it can out of a pasted Roblox link without a network call:
    /// <c>…/games/{placeId}/name?privateServerLinkCode={code}</c>, a plain game link,
    /// <c>…/share?code={code}&amp;type=Server</c> (resolved online later), and
    /// <c>roblox://experiences/start?placeId=…&amp;gameInstanceId=…</c> deep links.
    /// </summary>
    public static ParsedJoinLink Parse(string input)
    {
        long placeId = 0; string? linkCode = null, shareCode = null, jobId = null;
        try
        {
            string text = input.Trim();
            // "www.roblox.com/games/…" pasted without a scheme is still a link.
            if (!text.Contains("://", StringComparison.Ordinal) && text.Contains("roblox.com", StringComparison.OrdinalIgnoreCase))
                text = "https://" + text;

            var uri = new Uri(text);
            var q = HttpUtility.ParseQueryString(uri.Query);

            var m = Regex.Match(uri.AbsolutePath, @"/games/(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) long.TryParse(m.Groups[1].Value, out placeId);
            if (placeId == 0) long.TryParse(q["placeId"] ?? q["placeid"], out placeId);

            linkCode = q["privateServerLinkCode"] ?? q["linkCode"];

            string? instance = q["gameInstanceId"] ?? q["gameId"] ?? q["jobId"];
            jobId = NormalizeJobId(instance);

            string? code = q["code"];
            bool isShare = uri.AbsolutePath.Contains("share", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(q["type"], "Server", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(code) && isShare) shareCode = code;
        }
        catch { /* not a URI: nothing to extract */ }
        return new ParsedJoinLink(placeId,
            string.IsNullOrWhiteSpace(linkCode) ? null : linkCode,
            string.IsNullOrWhiteSpace(shareCode) ? null : shareCode,
            jobId);
    }

    /// <summary>
    /// Reads a Place ID box. A bare number is the id; a pasted game link yields the id from its path.
    /// Stripping every non-digit (what the boxes used to do) turned
    /// <c>/games/920587237/x?privateServerLinkCode=123</c> into place 920587237123.
    /// </summary>
    public static long ParsePlaceId(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return 0;
        if (long.TryParse(t, out long direct)) return direct > 0 ? direct : 0;
        if (LooksLikeLink(t)) return Parse(t).PlaceId;
        var digits = new string(t.Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 18 && long.TryParse(digits, out long id) && id > 0 ? id : 0;
    }
}
