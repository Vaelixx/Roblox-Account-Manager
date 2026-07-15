using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    /// Robust auth-ticket fetch. The ticket endpoint doubles as the CSRF source: an unauthenticated
    /// POST returns 403 + a fresh x-csrf-token, which we then replay. We retry on token rotation so a
    /// stale CSRF never surfaces as a false "expired cookie".
    /// </summary>
    public static async Task<(string? ticket, string error)> GetAuthTicketDetailedAsync(string cookie)
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
                    if (!string.IsNullOrEmpty(ticket)) return (ticket, "");
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

        return (null, lastError);
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
