using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services;

/// <summary>
/// Strips credentials out of free text before it is written to the diagnostics log or copied into a
/// bug report: Roblox session cookies, private-server link/access/share codes, Discord webhook URLs,
/// bearer/API tokens, and the user:password part of proxy URLs. Pattern-based, so it is a backstop —
/// code should still never put a secret into a message in the first place.
/// </summary>
public static class Redaction
{
    public const string Marker = "<redacted>";

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    private static readonly (Regex pattern, string replacement)[] Rules =
    {
        // .ROBLOSECURITY: the whole whitespace-delimited token that carries the warning prefix.
        (new Regex(@"\S*_\|WARNING:-DO-NOT-SHARE-THIS\S*", RegexOptions.CultureInvariant, Budget), "<cookie redacted>"),
        (new Regex(@"(\.ROBLOSECURITY\s*[=:]\s*)[^\s;,""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget), "$1" + Marker),
        // Private servers: classic link codes, access codes, share-link codes.
        (new Regex(@"((?:privateServerLinkCode|linkCode|accessCode)=)[^&\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget), "$1" + Marker),
        (new Regex(@"(/share\?[^\s""']*?\bcode=)[^&\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget), "$1" + Marker),
        // Discord webhooks: the id/token path is the secret.
        (new Regex(@"(discord(?:app)?\.com/api/webhooks/)[^\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget), "$1" + Marker),
        // Tokens in headers and query strings.
        (new Regex(@"(Bearer\s+)[A-Za-z0-9\-._~+/=]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget), "$1" + Marker),
        (new Regex(@"([?&](?:token|api_key|apikey|key|password|pwd)=)[^&\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget), "$1" + Marker),
        // Credentials embedded in a URL: scheme://user:password@host
        (new Regex(@"([a-z][a-z0-9+.\-]*://)[^\s/@:""']+:[^\s/@""']+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget), "$1" + Marker + "@"),
    };

    /// <summary>Returns <paramref name="text"/> with every recognised secret replaced. Never throws; fails closed.</summary>
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            foreach (var (pattern, replacement) in Rules)
                text = pattern.Replace(text, replacement);
            return text;
        }
        catch { return Marker; }   // a pattern blew its time budget: emit nothing rather than unverified text
    }
}
