using System.Collections.Concurrent;
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
/// Async wrapper over the Roblox web endpoints the manager needs.
///
/// Written to degrade rather than break when Roblox changes something:
/// <list type="bullet">
///   <item><description>every JSON field is read defensively — a renamed or missing field yields a default, not an exception;</description></item>
///   <item><description>read-only calls retry on HTTP 429 and 5xx with a short backoff (honouring Retry-After);</description></item>
///   <item><description>batch endpoints are chunked to Roblox's documented limits.</description></item>
/// </list>
/// Cookies are set per request (UseCookies=false), so one client per proxy serves every account.
/// </summary>
public static class RobloxApi
{
    /// <summary>Resolves the per-account proxy for a cookie; wired to <see cref="AccountStore.ProxyForCookie"/> at startup.</summary>
    public static Func<string, string?>? AccountProxyResolver { get; set; }

    private const int PresenceBatch = 50;
    private const int ThumbnailBatch = 100;
    private const int UsersBatch = 100;

    // ---------------------------------------------------------------
    //  HTTP plumbing
    // ---------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    /// <summary>
    /// Client for a request made with <paramref name="cookie"/>: the owning account's own proxy when it
    /// has one, otherwise the global proxy from settings, otherwise a direct connection.
    /// </summary>
    private static HttpClient HttpFor(string? cookie)
    {
        string? accountProxy = string.IsNullOrEmpty(cookie) ? null : SafeResolve(cookie);
        var s = SettingsService.Current;

        (string address, string user, string pass) proxy = accountProxy != null
            ? (accountProxy, "", "")
            : s.EnableProxy && !string.IsNullOrWhiteSpace(s.ProxyAddress)
                ? (s.ProxyAddress.Trim(), s.ProxyUsername, s.ProxyPassword)
                : ("", "", "");

        string signature = $"{proxy.address}|{proxy.user}|{proxy.pass}";
        if (_clients.TryGetValue(signature, out var existing)) return existing;

        // A user editing a proxy creates a client per variant typed; keep the cache from growing
        // without bound. In-flight requests hold their own reference, so disposal is delayed.
        if (_clients.Count > 16)
        {
            var stale = _clients.Values.ToList();
            _clients.Clear();
            _ = Task.Run(async () => { await Task.Delay(60_000); foreach (var c in stale) try { c.Dispose(); } catch { } });
        }

        return _clients.GetOrAdd(signature, _ => BuildClient(proxy.address, proxy.user, proxy.pass));
    }

    private static string? SafeResolve(string cookie)
    {
        try { return AccountProxyResolver?.Invoke(cookie); }
        catch { return null; }
    }

    private static HttpClient BuildClient(string proxyAddress, string user, string pass)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),   // pick up DNS changes on a long-running app
        };

        if (proxyAddress.Length > 0)
        {
            var proxy = TryBuildProxy(proxyAddress, user, pass);
            if (proxy != null) { handler.Proxy = proxy; handler.UseProxy = true; }
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Roblox/WinInet");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// Turns a user-typed proxy into a <see cref="WebProxy"/>. Accepts <c>host:port</c>, a full
    /// <c>http://</c> / <c>socks5://</c> URI, and credentials either separately or inline
    /// (<c>http://user:pass@host:port</c>). Returns null when it cannot be parsed, so a typo falls back
    /// to a direct connection instead of breaking every request.
    /// </summary>
    public static WebProxy? TryBuildProxy(string? address, string? user, string? password)
    {
        address = (address ?? "").Trim();
        if (address.Length == 0) return null;
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
        try
        {
            var uri = new Uri(address);
            if (uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")) return null;

            string inlineUser = "", inlinePass = "";
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                inlineUser = Uri.UnescapeDataString(parts[0]);
                inlinePass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            }

            var proxy = new WebProxy(new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri);
            string u = !string.IsNullOrEmpty(user) ? user : inlineUser;
            string p = !string.IsNullOrEmpty(user) ? (password ?? "") : inlinePass;
            if (!string.IsNullOrEmpty(u)) proxy.Credentials = new NetworkCredential(u, p);
            return proxy;
        }
        catch { return null; }
    }

    /// <summary>Host and port of a proxy string for a Chromium --proxy-server switch, or null.</summary>
    public static string? ProxyServerArgument(string? address)
    {
        var proxy = TryBuildProxy(address, null, null);
        if (proxy?.Address == null) return null;
        var a = proxy.Address;
        return a.Scheme == "http" ? $"{a.Host}:{a.Port}" : $"{a.Scheme}://{a.Host}:{a.Port}";
    }

    /// <summary>
    /// Sends one request through a proxy and reports whether Roblox answered. Backs "Test proxy" —
    /// a proxy that silently swallows traffic is otherwise only noticed when every account looks offline.
    /// </summary>
    public static async Task<(bool ok, string message)> TestProxyAsync(string address, string user, string password)
    {
        var proxy = TryBuildProxy(address, user, password);
        if (proxy == null) return (false, L.T("Proxy.Test.Unparsable"));

        using var handler = new SocketsHttpHandler { Proxy = proxy, UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Roblox/WinInet");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var resp = await client.GetAsync("https://users.roblox.com/v1/users/1");
            sw.Stop();
            return resp.IsSuccessStatusCode
                ? (true, L.T("Proxy.Test.Ok", sw.ElapsedMilliseconds))
                : (false, L.T("Proxy.Test.Http", (int)resp.StatusCode));
        }
        catch (Exception ex) { return (false, L.T("Proxy.Test.Failed", ex.Message)); }
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

    /// <summary>
    /// Sends a read-only request, retrying on 429 / 5xx / transport errors. The factory is called per
    /// attempt because an HttpRequestMessage cannot be sent twice.
    /// </summary>
    private static async Task<HttpResponseMessage> SendReadAsync(Func<HttpRequestMessage> make, string? cookie, int attempts = 3)
    {
        var http = HttpFor(cookie);
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(make()).ConfigureAwait(false);
            }
            catch (Exception) when (attempt < attempts)
            {
                await Task.Delay(Backoff(attempt, null)).ConfigureAwait(false);
                continue;
            }

            int code = (int)resp.StatusCode;
            if (attempt >= attempts || (code != 429 && code < 500)) return resp;

            var delay = Backoff(attempt, resp.Headers.RetryAfter);
            resp.Dispose();
            await Task.Delay(delay).ConfigureAwait(false);
        }
    }

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta < TimeSpan.FromSeconds(8) ? delta : TimeSpan.FromSeconds(8);
        return TimeSpan.FromMilliseconds(500 * attempt + Random.Shared.Next(0, 250));
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode) return null;
        try { return JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)); }
        catch { return null; }
    }

    // ---- defensive JSON readers ----
    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    private static double Double(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IEnumerable<JsonElement> Items(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static IEnumerable<long[]> Chunks(IEnumerable<long> ids, int size)
        => ids.Where(i => i > 0).Distinct().Chunk(size);

    // ---------------------------------------------------------------
    //  Identity / validation
    // ---------------------------------------------------------------
    public record Identity(long Id, string Name, string DisplayName);

    /// <summary>Rich presence for one user, including the join target for follow-launch.</summary>
    public record PresenceDetail(string Status, string LastLocation, long PlaceId, long RootPlaceId, long UniverseId, string? JobId);

    public static async Task<Identity?> GetAuthenticatedUserAsync(string cookie)
        => (await GetAuthenticatedUserDetailedAsync(cookie)).Identity;

    /// <summary>
    /// The account behind a cookie, plus whether a null result actually <em>proves</em> the cookie is
    /// dead. Only 401/403 do; 429 (rate limiting, which a batch of launches provokes by itself), 5xx
    /// and transport failures say nothing about the cookie and must not mark an account invalid.
    /// </summary>
    public static async Task<(Identity? Identity, bool CookieRejected)> GetAuthenticatedUserDetailedAsync(string cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return (null, true);
        try
        {
            using var resp = await SendReadAsync(() => Build(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie), cookie);
            if (!resp.IsSuccessStatusCode)
                return (null, (int)resp.StatusCode is 401 or 403);

            using var doc = await ReadJsonAsync(resp);
            if (doc == null) return (null, false);
            var root = doc.RootElement;
            long id = Long(root, "id");
            return id > 0 ? (new Identity(id, Str(root, "name"), Str(root, "displayName")), false) : (null, false);
        }
        catch { return (null, false); }
    }

    // ---------------------------------------------------------------
    //  CSRF + auth ticket
    // ---------------------------------------------------------------
    public static async Task<string?> GetCsrfTokenAsync(string cookie)
    {
        try
        {
            using var resp = await HttpFor(cookie).SendAsync(Build(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie,
                referer: "https://www.roblox.com/"));
            return resp.Headers.TryGetValues("x-csrf-token", out var vals) ? vals.FirstOrDefault() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// A rotated .ROBLOSECURITY from a Set-Cookie header — only a genuine token (it carries the WARNING
    /// prefix) that differs from <paramref name="current"/>; deletions and placeholders are ignored.
    /// </summary>
    private static string? ExtractRotatedCookie(HttpResponseMessage resp, string current)
    {
        if (!resp.Headers.TryGetValues("set-cookie", out var cookies)) return null;
        foreach (var raw in cookies)
        {
            var m = Regex.Match(raw, @"\.ROBLOSECURITY=([^;]+)");
            if (!m.Success) continue;
            string val = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(val) || !val.Contains("WARNING")) continue;
            return val == current ? null : val;
        }
        return null;
    }

    /// <summary>
    /// Auth-ticket fetch for launching. The endpoint doubles as the CSRF source: a POST without a
    /// token is answered 403 + x-csrf-token, which is then replayed. Retries on token rotation so a
    /// stale token never reads as an expired cookie, and surfaces a rotated .ROBLOSECURITY.
    /// </summary>
    public static async Task<(string? ticket, string? rotatedCookie, string error)> GetAuthTicketDetailedAsync(string cookie)
    {
        string? csrf = await GetCsrfTokenAsync(cookie);
        string lastError = "no response";
        var http = HttpFor(cookie);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                // Roblox rejects the ticket POST with 415 unless it carries a JSON body.
                var body = new StringContent("{}", Encoding.UTF8);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var req = Build(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/", cookie, csrf,
                    content: body, referer: "https://www.roblox.com/");
                req.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
                req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");

                using var resp = await http.SendAsync(req);

                if (resp.Headers.TryGetValues("rbx-authentication-ticket", out var t))
                {
                    string? ticket = t.FirstOrDefault();
                    if (!string.IsNullOrEmpty(ticket)) return (ticket, ExtractRotatedCookie(resp, cookie), "");
                }

                if (resp.StatusCode == HttpStatusCode.Forbidden &&
                    resp.Headers.TryGetValues("x-csrf-token", out var nt) && !string.IsNullOrEmpty(nt.FirstOrDefault()))
                {
                    csrf = nt.First();
                    lastError = "csrf rotated";
                    continue;
                }

                lastError = $"HTTP {(int)resp.StatusCode}";
                if (resp.StatusCode == HttpStatusCode.Unauthorized) break;   // genuinely signed out
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    await Task.Delay(Backoff(attempt + 1, resp.Headers.RetryAfter));
            }
            catch (Exception ex) { lastError = ex.Message; }
        }

        return (null, null, lastError);
    }

    // ---------------------------------------------------------------
    //  Economy
    // ---------------------------------------------------------------
    public static async Task<long> GetRobuxAsync(string cookie)
    {
        try
        {
            using var resp = await SendReadAsync(() => Build(HttpMethod.Get, "https://economy.roblox.com/v1/user/currency", cookie), cookie);
            using var doc = await ReadJsonAsync(resp);
            if (doc == null || !doc.RootElement.TryGetProperty("robux", out var r) || r.ValueKind != JsonValueKind.Number) return -1;
            return r.GetInt64();
        }
        catch { return -1; }
    }

    /// <summary>
    /// Sum of recent-average-price over the user's collectibles ("RAP"). Capped at 10 pages (1000
    /// items) so a large inventory cannot stall the refresh. (-1, 0) when private or failed.
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
                using var resp = await SendReadAsync(() => Build(HttpMethod.Get, url, cookie), cookie);
                using var doc = await ReadJsonAsync(resp);
                if (doc == null) { if (page == 0) return (-1, 0); break; }
                var root = doc.RootElement;
                foreach (var item in Items(root, "data"))
                {
                    rap += Long(item, "recentAveragePrice");
                    count++;
                }
                cursor = Str(root, "nextPageCursor");
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
            using var resp = await SendReadAsync(() => Build(HttpMethod.Get,
                $"https://premiumfeatures.roblox.com/v1/users/{userId}/validate-membership", cookie), cookie);
            if (!resp.IsSuccessStatusCode) return false;
            var body = (await resp.Content.ReadAsStringAsync()).Trim();
            return body.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------
    //  Presence
    // ---------------------------------------------------------------

    /// <summary>
    /// Rich presence (status, game name, join target) for any number of users, in batches of 50.
    /// Returns an empty map when every batch failed, so callers can keep the last known state.
    /// </summary>
    public static async Task<Dictionary<long, PresenceDetail>> GetPresenceDetailsAsync(string cookie, IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, PresenceDetail>();
        foreach (var batch in Chunks(userIds, PresenceBatch))
        {
            try
            {
                using var resp = await SendReadAsync(() => Build(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie,
                    content: Json(new { userIds = batch })), cookie);
                using var doc = await ReadJsonAsync(resp);
                if (doc == null) continue;
                foreach (var p in Items(doc.RootElement, "userPresences"))
                {
                    long id = Long(p, "userId");
                    if (id <= 0) continue;
                    string? job = Str(p, "gameId");
                    result[id] = new PresenceDetail(
                        PresenceStatus.FromType(Int(p, "userPresenceType")),
                        Str(p, "lastLocation"),
                        Long(p, "placeId"),
                        Long(p, "rootPlaceId"),
                        Long(p, "universeId"),
                        string.IsNullOrEmpty(job) ? null : job);
                }
            }
            catch { /* one failed batch must not lose the others */ }
        }
        return result;
    }

    // ---------------------------------------------------------------
    //  Friends
    // ---------------------------------------------------------------

    /// <summary>
    /// Full friends list of <paramref name="userId"/>. Uses the paged /friends/find endpoint (the
    /// one roblox.com uses now) and falls back to the legacy list. Neither reliably returns names any
    /// more, so missing names are resolved in batches through the users API.
    /// </summary>
    public static async Task<List<Friend>> GetFriendsAsync(string cookie, long userId)
    {
        var ids = new List<long>();
        var names = new Dictionary<long, (string name, string disp)>();
        if (userId <= 0) return new List<Friend>();

        bool paged = false;
        try
        {
            string cursor = "";
            for (int page = 0; page < 40; page++)
            {
                string url = $"https://friends.roblox.com/v1/users/{userId}/friends/find?limit=50"
                             + (cursor.Length > 0 ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
                using var resp = await SendReadAsync(() => Build(HttpMethod.Get, url, cookie), cookie);
                using var doc = await ReadJsonAsync(resp);
                if (doc == null) break;
                paged = true;
                foreach (var f in Items(doc.RootElement, "PageItems"))
                {
                    long id = Long(f, "id");
                    if (id > 0) ids.Add(id);
                }
                cursor = Str(doc.RootElement, "NextCursor");
                if (cursor.Length == 0) break;
            }
        }
        catch { }

        if (!paged)
        {
            try
            {
                using var resp = await SendReadAsync(() => Build(HttpMethod.Get, $"https://friends.roblox.com/v1/users/{userId}/friends", cookie), cookie);
                using var doc = await ReadJsonAsync(resp);
                if (doc != null)
                    foreach (var f in Items(doc.RootElement, "data"))
                    {
                        long id = Long(f, "id");
                        if (id <= 0) continue;
                        ids.Add(id);
                        string n = Str(f, "name"), d = Str(f, "displayName");
                        if (n.Length > 0) names[id] = (n, d);
                    }
            }
            catch { }
        }

        ids = ids.Distinct().ToList();
        var missing = ids.Where(i => !names.ContainsKey(i)).ToList();
        if (missing.Count > 0)
            foreach (var kv in await GetUsersInfoAsync(missing))
                names[kv.Key] = kv.Value;

        return ids.Select(id => names.TryGetValue(id, out var n)
                ? new Friend { UserId = id, Username = n.name, DisplayName = n.disp }
                : new Friend { UserId = id })
            .ToList();
    }

    /// <summary>Batch username / display-name lookup (public users API, no cookie needed).</summary>
    public static async Task<Dictionary<long, (string name, string disp)>> GetUsersInfoAsync(IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, (string name, string disp)>();
        foreach (var batch in Chunks(userIds, UsersBatch))
        {
            try
            {
                using var resp = await SendReadAsync(() => Build(HttpMethod.Post, "https://users.roblox.com/v1/users", "",
                    content: Json(new { userIds = batch, excludeBannedUsers = false })), null);
                using var doc = await ReadJsonAsync(resp);
                if (doc == null) continue;
                foreach (var u in Items(doc.RootElement, "data"))
                {
                    long id = Long(u, "id");
                    if (id > 0) result[id] = (Str(u, "name"), Str(u, "displayName"));
                }
            }
            catch { }
        }
        return result;
    }

    // ---------------------------------------------------------------
    //  Thumbnails
    // ---------------------------------------------------------------
    public static async Task<Dictionary<long, string>> GetHeadshotsAsync(IEnumerable<long> userIds)
    {
        var result = new Dictionary<long, string>();
        foreach (var batch in Chunks(userIds, ThumbnailBatch))
        {
            try
            {
                string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={string.Join(",", batch)}&size=150x150&format=Png&isCircular=false";
                using var resp = await SendReadAsync(() => new HttpRequestMessage(HttpMethod.Get, url), null);
                using var doc = await ReadJsonAsync(resp);
                if (doc == null) continue;
                foreach (var t in Items(doc.RootElement, "data"))
                {
                    long id = Long(t, "targetId");
                    string img = Str(t, "imageUrl");
                    if (id > 0 && img.Length > 0) result[id] = img;
                }
            }
            catch { }
        }
        return result;
    }

    // ---------------------------------------------------------------
    //  Username -> UserId
    // ---------------------------------------------------------------
    public static async Task<long> GetUserIdAsync(string username)
    {
        try
        {
            using var resp = await SendReadAsync(() => Build(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users", "",
                content: Json(new { usernames = new[] { username }, excludeBannedUsers = false })), null);
            using var doc = await ReadJsonAsync(resp);
            if (doc == null) return -1;
            var first = Items(doc.RootElement, "data").FirstOrDefault();
            return first.ValueKind == JsonValueKind.Object ? Long(first, "id") : -1;
        }
        catch { return -1; }
    }

    // ---------------------------------------------------------------
    //  Game / place info
    // ---------------------------------------------------------------
    public record PlaceInfo(long PlaceId, long UniverseId, string Name, string Creator, long RootPlaceId)
    {
        /// <summary>True when the place is a sub-place (teleport/co-edit) rather than the universe's start place.</summary>
        public bool IsSubPlace => PlaceId > 0 && RootPlaceId > 0 && PlaceId != RootPlaceId;
    }

    public static async Task<PlaceInfo?> GetPlaceInfoAsync(string cookie, long placeId)
    {
        try
        {
            using var uResp = await SendReadAsync(() => Build(HttpMethod.Get,
                $"https://apis.roblox.com/universes/v1/places/{placeId}/universe", cookie), cookie);
            using var uDoc = await ReadJsonAsync(uResp);
            long universeId = uDoc == null ? 0 : Long(uDoc.RootElement, "universeId");
            if (universeId <= 0) return null;

            using var gResp = await SendReadAsync(() => Build(HttpMethod.Get,
                $"https://games.roblox.com/v1/games?universeIds={universeId}", cookie), cookie);
            using var gDoc = await ReadJsonAsync(gResp);
            var first = gDoc == null ? default : Items(gDoc.RootElement, "data").FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
                return new PlaceInfo(placeId, universeId, L.T("Place.Fallback", placeId), "", 0);

            string name = Str(first, "name");
            string creator = first.TryGetProperty("creator", out var c) && c.ValueKind == JsonValueKind.Object ? Str(c, "name") : "";
            return new PlaceInfo(placeId, universeId, name.Length > 0 ? name : L.T("Place.Fallback", placeId), creator, Long(first, "rootPlaceId"));
        }
        catch { return null; }
    }

    public static async Task<string?> GetGameIconAsync(long universeId)
    {
        if (universeId <= 0) return null;
        try
        {
            string url = $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size=150x150&format=Png&isCircular=false";
            using var resp = await SendReadAsync(() => new HttpRequestMessage(HttpMethod.Get, url), null);
            using var doc = await ReadJsonAsync(resp);
            var first = doc == null ? default : Items(doc.RootElement, "data").FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object) return null;
            string img = Str(first, "imageUrl");
            return img.Length > 0 ? img : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------
    //  Private-server / join-link parsing
    // ---------------------------------------------------------------
    public record ShareLinkInfo(long PlaceId, string LinkCode);

    /// <summary>Resolves a modern share link (roblox.com/share?code=…&amp;type=Server) to a place and link code.</summary>
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
                using var resp = await HttpFor(cookie).SendAsync(req);

                if (resp.StatusCode == HttpStatusCode.Forbidden &&
                    resp.Headers.TryGetValues("x-csrf-token", out var nt))
                { csrf = nt.FirstOrDefault(); continue; }

                using var doc = await ReadJsonAsync(resp);
                if (doc == null || !doc.RootElement.TryGetProperty("privateServerInviteData", out var d) || d.ValueKind != JsonValueKind.Object)
                    return null;
                long placeId = Long(d, "placeId");
                string linkCode = Str(d, "linkCode");
                return placeId > 0 && linkCode.Length > 0 ? new ShareLinkInfo(placeId, linkCode) : null;
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
                             + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                using var resp = await SendReadAsync(() => new HttpRequestMessage(HttpMethod.Get, url), null, attempts: 4);
                using var doc = await ReadJsonAsync(resp);
                if (doc == null) break;
                var root = doc.RootElement;

                foreach (var s in Items(root, "data"))
                {
                    string id = Str(s, "id");
                    if (id.Length == 0) continue;
                    servers.Add(new GameServer
                    {
                        Id = id,
                        Playing = Int(s, "playing"),
                        MaxPlayers = Int(s, "maxPlayers"),
                        Fps = Double(s, "fps"),
                        Ping = Int(s, "ping"),
                        IsVip = false
                    });
                }

                cursor = Str(root, "nextPageCursor");
                page++;
            }
            while (!string.IsNullOrEmpty(cursor) && page < maxPages);
        }
        catch { }

        return servers;
    }

    /// <summary>A random non-full public job id (smart join), or the emptiest when <paramref name="lowest"/> is set.</summary>
    public static async Task<string> GetRandomJobIdAsync(long placeId, bool lowest, int maxPages)
    {
        var servers = await GetPublicServersAsync(placeId, maxPages);
        var valid = servers.Where(s => s.Playing > 0 && s.Playing < s.MaxPlayers && s.MaxPlayers > 1).ToList();
        if (valid.Count == 0) valid = servers.Where(s => s.Playing < s.MaxPlayers).ToList();
        if (valid.Count == 0) return "";
        if (lowest) return valid.OrderBy(s => s.Playing).First().Id;
        return valid[Random.Shared.Next(valid.Count)].Id;
    }
}
