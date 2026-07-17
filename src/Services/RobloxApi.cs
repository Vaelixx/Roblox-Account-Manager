using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Thin async wrapper over the Roblox web endpoints the manager needs.
/// Cookies are set per-request (UseCookies=false) so one client serves every account.
/// </summary>
public static class RobloxApi
{
    private static readonly HttpClient Http;

    static RobloxApi()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Roblox/WinInet");
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, string cookie,
        string? csrf = null, HttpContent? content = null, string? referer = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(cookie))
            req.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cookie}");
        if (csrf != null) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (content != null) req.Content = content;
        return req;
    }

    private static StringContent Json(object o)
        => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    // ---------------------------------------------------------------
    //  Identity / validation
    // ---------------------------------------------------------------
    public record Identity(long Id, string Name, string DisplayName);

    /// <summary>Rich presence for one user, including the join target for follow-launch.</summary>
    public record PresenceDetail(string Status, string LastLocation, long PlaceId, long RootPlaceId, long UniverseId, string? JobId);

    public static async Task<Identity?> GetAuthenticatedUserAsync(string cookie)
    {
        try
        {
            var resp = await Http.SendAsync(Build(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return new Identity(
                root.GetProperty("id").GetInt64(),
                root.GetProperty("name").GetString() ?? "",
                root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "");
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------
    //  CSRF + auth ticket
    // ---------------------------------------------------------------
    public static async Task<string?> GetCsrfTokenAsync(string cookie)
    {
        try
        {
            var resp = await Http.SendAsync(Build(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie,
                referer: "https://www.roblox.com/"));
            if (resp.Headers.TryGetValues("x-csrf-token", out var vals))
                return vals.FirstOrDefault();
            return null;
        }
        catch { return null; }
    }

    public static async Task<string?> GetAuthTicketAsync(string cookie)
        => (await GetAuthTicketDetailedAsync(cookie)).ticket;

    /// <summary>
    /// Roblox occasionally rotates the .ROBLOSECURITY cookie and hands back the new value in a
    /// Set-Cookie header. Returns that value only when it is a genuine token (carries the WARNING
    /// prefix) that differs from <paramref name="current"/>; deletion/placeholder cookies are ignored.
    /// </summary>
    private static string? ExtractRotatedCookie(HttpResponseMessage resp, string current)
    {
        if (!resp.Headers.TryGetValues("set-cookie", out var cookies)) return null;
        foreach (var raw in cookies)
        {
            var m = Regex.Match(raw, @"\.ROBLOSECURITY=([^;]+)");
            if (!m.Success) continue;
            string val = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(val) || !val.Contains("WARNING")) continue; // ignore deletes
            return val == current ? null : val;
        }
        return null;
    }

    /// <summary>
    /// Robust auth-ticket fetch. The ticket endpoint doubles as the CSRF source: an unauthenticated
    /// POST returns 403 + a fresh x-csrf-token, which we then replay. We retry on token rotation so a
    /// stale CSRF never surfaces as a false "expired cookie". Also surfaces a rotated .ROBLOSECURITY
    /// (<c>rotatedCookie</c>) when Roblox issues one, so the caller can persist it.
    /// </summary>
    public static async Task<(string? ticket, string? rotatedCookie, string error)> GetAuthTicketDetailedAsync(string cookie)
    {
        string? csrf = await GetCsrfTokenAsync(cookie);
        string lastError = "no response";

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Roblox rejects the ticket POST with 415 unless it carries a JSON body/content-type.
                var body = new StringContent("{}", Encoding.UTF8);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var req = Build(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/", cookie, csrf,
                    content: body, referer: "https://www.roblox.com/games/4924922222/");
                req.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
                req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");

                var resp = await Http.SendAsync(req);

                if (resp.Headers.TryGetValues("rbx-authentication-ticket", out var t))
                {
                    string? ticket = t.FirstOrDefault();
                    if (!string.IsNullOrEmpty(ticket)) return (ticket, ExtractRotatedCookie(resp, cookie), "");
                }

                // CSRF rotated? grab the fresh token and replay.
                if (resp.StatusCode == HttpStatusCode.Forbidden &&
                    resp.Headers.TryGetValues("x-csrf-token", out var nt) && !string.IsNullOrEmpty(nt.FirstOrDefault()))
                {
                    csrf = nt.First();
                    lastError = "csrf rotated";
                    continue;
                }

                lastError = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
                if (resp.StatusCode == HttpStatusCode.Unauthorized) break; // genuinely signed out
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        return (null, null, lastError);
    }

    // ---------------------------------------------------------------
    //  Robux
    // ---------------------------------------------------------------
    public static async Task<long> GetRobuxAsync(string cookie)
    {
        try
        {
            var resp = await Http.SendAsync(Build(HttpMethod.Get, "https://economy.roblox.com/v1/user/currency", cookie));
            if (!resp.IsSuccessStatusCode) return -1;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("robux").GetInt64();
        }
        catch { return -1; }
    }

    /// <summary>
    /// Sums the recent-average-price of every collectible the user owns (their "RAP").
    /// Paginates the inventory endpoint, capped at 10 pages (1000 items) so a whale
    /// account can't stall the refresh. Returns (-1, 0) when the inventory is private
    /// or the request fails.
    /// </summary>
    public static async Task<(long rap, int count)> GetCollectiblesRapAsync(string cookie, long userId)
    {
        long rap = 0; int count = 0;
        try
        {
            string cursor = "";
            for (int page = 0; page < 10; page++)
            {
                string url = $"https://inventory.roblox.com/v1/users/{userId}/assets/collectibles?limit=100&sortOrder=Asc";
                if (!string.IsNullOrEmpty(cursor)) url += $"&cursor={Uri.EscapeDataString(cursor)}";
                var resp = await Http.SendAsync(Build(HttpMethod.Get, url, cookie));
                if (!resp.IsSuccessStatusCode) { if (page == 0) return (-1, 0); break; }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data))
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("recentAveragePrice", out var rp) && rp.ValueKind == JsonValueKind.Number)
                            rap += rp.GetInt64();
                        count++;
                    }
                cursor = root.TryGetProperty("nextPageCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(cursor)) break;
            }
            return (rap, count);
        }
        catch { return (-1, 0); }
    }

    /// <summary>True when the account currently holds a Roblox Premium membership.</summary>
    public static async Task<bool> GetPremiumAsync(string cookie, long userId)
    {
        try
        {
            var resp = await Http.SendAsync(Build(HttpMethod.Get,
                $"https://premiumfeatures.roblox.com/v1/users/{userId}/validate-membership", cookie));
            if (!resp.IsSuccessStatusCode) return false;
            var body = (await resp.Content.ReadAsStringAsync()).Trim();
            return body.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------
    //  Presence
    // ---------------------------------------------------------------
    public static async Task<Dictionary<long, string>> GetPresencesAsync(string cookie, IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, string>();
        var ids = userIds.Where(i => i > 0).Distinct().ToArray();
        if (ids.Length == 0) return result;

        try
        {
            var resp = await Http.SendAsync(Build(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie,
                content: Json(new { userIds = ids })));
            if (!resp.IsSuccessStatusCode) return result;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var p in doc.RootElement.GetProperty("userPresences").EnumerateArray())
            {
                long id = p.GetProperty("userId").GetInt64();
                int type = p.GetProperty("userPresenceType").GetInt32();
                result[id] = type switch { 1 => "Online", 2 => "In Game", 3 => "In Studio", _ => "Offline" };
            }
        }
        catch { }
        return result;
    }

    // ---------------------------------------------------------------
    //  Friends
    // ---------------------------------------------------------------
    /// <summary>
    /// Full friends list of <paramref name="userId"/>. Sent with the account's own cookie
    /// so private display names / friendships resolve correctly.
    /// </summary>
    public static async Task<List<Friend>> GetFriendsAsync(string cookie, long userId)
    {
        var result = new List<Friend>();
        if (userId <= 0) return result;
        try
        {
            var resp = await Http.SendAsync(Build(HttpMethod.Get,
                $"https://friends.roblox.com/v1/users/{userId}/friends", cookie));
            if (!resp.IsSuccessStatusCode) return result;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("data", out var data)) return result;
            foreach (var f in data.EnumerateArray())
            {
                long id = f.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
                if (id <= 0) continue;
                string name = f.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                string disp = f.TryGetProperty("displayName", out var dEl) ? (dEl.GetString() ?? "") : "";
                result.Add(new Friend { UserId = id, Username = name, DisplayName = disp });
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Rich presence (status + last location + join target) for a batch of user ids.
    /// Unlike <see cref="GetPresencesAsync"/> this also returns place / job id so a friend
    /// can be followed into their game directly.
    /// </summary>
    public static async Task<Dictionary<long, PresenceDetail>> GetPresenceDetailsAsync(string cookie, IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, PresenceDetail>();
        var ids = userIds.Where(i => i > 0).Distinct().ToArray();
        if (ids.Length == 0) return result;
        try
        {
            var resp = await Http.SendAsync(Build(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie,
                content: Json(new { userIds = ids })));
            if (!resp.IsSuccessStatusCode) return result;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var p in doc.RootElement.GetProperty("userPresences").EnumerateArray())
            {
                long id = p.GetProperty("userId").GetInt64();
                int type = p.TryGetProperty("userPresenceType", out var t) ? t.GetInt32() : 0;
                string status = type switch { 1 => "Online", 2 => "In Game", 3 => "In Studio", _ => "Offline" };
                string loc = p.TryGetProperty("lastLocation", out var l) ? (l.GetString() ?? "") : "";
                long place = p.TryGetProperty("placeId", out var pe)     && pe.ValueKind == JsonValueKind.Number ? pe.GetInt64() : 0;
                long root  = p.TryGetProperty("rootPlaceId", out var re) && re.ValueKind == JsonValueKind.Number ? re.GetInt64() : 0;
                long uni   = p.TryGetProperty("universeId", out var ue)  && ue.ValueKind == JsonValueKind.Number ? ue.GetInt64() : 0;
                string? job = p.TryGetProperty("gameId", out var ge)     && ge.ValueKind == JsonValueKind.String ? ge.GetString() : null;
                result[id] = new PresenceDetail(status, loc, place, root, uni, job);
            }
        }
        catch { }
        return result;
    }

    // ---------------------------------------------------------------
    //  Thumbnails (headshots)
    // ---------------------------------------------------------------
    public static async Task<Dictionary<long, string>> GetHeadshotsAsync(IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, string>();
        var ids = userIds.Where(i => i > 0).Distinct().ToArray();
        if (ids.Length == 0) return result;

        try
        {
            string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={string.Join(",", ids)}&size=150x150&format=Png&isCircular=false";
            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return result;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var t in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                long id = t.GetProperty("targetId").GetInt64();
                string? img = t.TryGetProperty("imageUrl", out var iu) ? iu.GetString() : null;
                if (!string.IsNullOrEmpty(img)) result[id] = img!;
            }
        }
        catch { }
        return result;
    }

    // ---------------------------------------------------------------
    //  Username -> UserId
    // ---------------------------------------------------------------
    public static async Task<long> GetUserIdAsync(string username)
    {
        try
        {
            var content = Json(new { usernames = new[] { username }, excludeBannedUsers = false });
            var resp = await Http.PostAsync("https://users.roblox.com/v1/usernames/users", content);
            if (!resp.IsSuccessStatusCode) return -1;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return -1;
            return data[0].GetProperty("id").GetInt64();
        }
        catch { return -1; }
    }

    // ---------------------------------------------------------------
    //  Game / place info
    // ---------------------------------------------------------------
    public record PlaceInfo(long PlaceId, long UniverseId, string Name, string Creator);

    public static async Task<PlaceInfo?> GetPlaceInfoAsync(string cookie, long placeId)
    {
        try
        {
            // placeId -> universeId
            var uResp = await Http.SendAsync(Build(HttpMethod.Get,
                $"https://apis.roblox.com/universes/v1/places/{placeId}/universe", cookie));
            if (!uResp.IsSuccessStatusCode) return null;
            using var uDoc = JsonDocument.Parse(await uResp.Content.ReadAsStringAsync());
            long universeId = uDoc.RootElement.GetProperty("universeId").GetInt64();

            var gResp = await Http.SendAsync(Build(HttpMethod.Get,
                $"https://games.roblox.com/v1/games?universeIds={universeId}", cookie));
            if (!gResp.IsSuccessStatusCode) return new PlaceInfo(placeId, universeId, $"Place {placeId}", "");
            using var gDoc = JsonDocument.Parse(await gResp.Content.ReadAsStringAsync());
            var data = gDoc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return new PlaceInfo(placeId, universeId, $"Place {placeId}", "");
            var first = data[0];
            string name = first.GetProperty("name").GetString() ?? $"Place {placeId}";
            string creator = first.TryGetProperty("creator", out var c) && c.TryGetProperty("name", out var cn)
                ? cn.GetString() ?? "" : "";
            return new PlaceInfo(placeId, universeId, name, creator);
        }
        catch { return null; }
    }

    public static async Task<string?> GetGameIconAsync(long universeId)
    {
        if (universeId <= 0) return null;
        try
        {
            string url = $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size=150x150&format=Png&isCircular=false";
            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;
            return data[0].TryGetProperty("imageUrl", out var iu) ? iu.GetString() : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------
    //  Private-server / join-link parsing
    // ---------------------------------------------------------------
    public record ParsedJoinLink(long PlaceId, string? LinkCode, string? ShareCode);

    /// <summary>
    /// Pulls what we can straight out of a pasted Roblox link WITHOUT a network call:
    ///   - classic private link:  .../games/{placeId}/name?privateServerLinkCode={code}
    ///   - plain game link:        .../games/{placeId}/...
    ///   - share link:             .../share?code={code}&amp;type=Server  (resolve via ResolveShareLinkAsync)
    /// </summary>
    public static ParsedJoinLink ParseJoinLink(string input)
    {
        long placeId = 0; string? linkCode = null; string? shareCode = null;
        try
        {
            var uri = new Uri(input.Trim());
            var q = HttpUtility.ParseQueryString(uri.Query);

            var m = Regex.Match(uri.AbsolutePath, @"/games/(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) long.TryParse(m.Groups[1].Value, out placeId);

            linkCode = q["privateServerLinkCode"];

            string? code = q["code"];
            string? type = q["type"];
            bool isShare = uri.AbsolutePath.Contains("share", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(type, "Server", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(code) && isShare) shareCode = code;
        }
        catch { }
        return new ParsedJoinLink(placeId,
            string.IsNullOrWhiteSpace(linkCode) ? null : linkCode,
            string.IsNullOrWhiteSpace(shareCode) ? null : shareCode);
    }

    public record ShareLinkInfo(long PlaceId, string LinkCode);

    /// <summary>Resolves a modern share link (roblox.com/share?code=...&amp;type=Server) to a place + link code.</summary>
    public static async Task<ShareLinkInfo?> ResolveShareLinkAsync(string cookie, string shareCode)
    {
        try
        {
            string? csrf = await GetCsrfTokenAsync(cookie);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var req = Build(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link",
                    cookie, csrf, content: Json(new { linkId = shareCode, linkType = "Server" }),
                    referer: "https://www.roblox.com/");
                var resp = await Http.SendAsync(req);

                if (resp.StatusCode == HttpStatusCode.Forbidden &&
                    resp.Headers.TryGetValues("x-csrf-token", out var nt))
                { csrf = nt.FirstOrDefault(); continue; }

                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("privateServerInviteData", out var d)) return null;
                long placeId = d.TryGetProperty("placeId", out var pid) ? pid.GetInt64() : 0;
                string? linkCode = d.TryGetProperty("linkCode", out var lc) ? lc.GetString() : null;
                if (placeId <= 0 || string.IsNullOrEmpty(linkCode)) return null;
                return new ShareLinkInfo(placeId, linkCode!);
            }
            return null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------
    //  Server browser
    // ---------------------------------------------------------------
    public static async Task<List<GameServer>> GetPublicServersAsync(long placeId, int maxPages = 5)
    {
        var servers = new List<GameServer>();
        string cursor = "";
        int page = 0;

        try
        {
            do
            {
                string url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder=Asc&limit=100"
                             + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={cursor}");
                var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) break;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data)) break;

                foreach (var s in data.EnumerateArray())
                {
                    servers.Add(new GameServer
                    {
                        Id = s.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Playing = s.TryGetProperty("playing", out var pl) ? pl.GetInt32() : 0,
                        MaxPlayers = s.TryGetProperty("maxPlayers", out var mp) ? mp.GetInt32() : 0,
                        Fps = s.TryGetProperty("fps", out var fps) ? fps.GetDouble() : 0,
                        Ping = s.TryGetProperty("ping", out var pg) ? pg.GetInt32() : 0,
                        IsVip = false
                    });
                }

                cursor = root.TryGetProperty("nextPageCursor", out var nc) && nc.ValueKind == JsonValueKind.String
                    ? nc.GetString() ?? "" : "";
                page++;
            }
            while (!string.IsNullOrEmpty(cursor) && page < maxPages);
        }
        catch { }

        return servers;
    }

    /// <summary>Pick a random non-full public job id (used by the "smart join" shuffle).</summary>
    public static async Task<string> GetRandomJobIdAsync(long placeId, bool lowest, int maxPages)
    {
        var servers = await GetPublicServersAsync(placeId, maxPages);
        var valid = servers.Where(s => s.Playing > 0 && s.Playing < s.MaxPlayers && s.MaxPlayers > 1).ToList();
        if (valid.Count == 0) return "";
        if (lowest) return valid.OrderBy(s => s.Playing).First().Id;
        return valid[Random.Shared.Next(valid.Count)].Id;
    }
}
