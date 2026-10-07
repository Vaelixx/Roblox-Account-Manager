using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services;

/// <summary>
/// Signs in to Roblox with a username and password and hands back the resulting
/// <c>.ROBLOSECURITY</c> cookie, so adding an account no longer requires the user to dig a
/// cookie out of their browser's developer tools.
///
/// The credentials are posted to Roblox's own login endpoint over HTTPS and are never written
/// to disk, never logged and never sent anywhere else — only the cookie Roblox returns is kept.
///
/// Roblox protects this endpoint with two kinds of interruption:
///
/// <list type="bullet">
///   <item><description><b>Two-step verification</b> — solvable here: the caller collects the
///   6-digit code and calls <see cref="CompleteTwoStepAsync"/>.</description></item>
///   <item><description><b>An interactive challenge</b> (the Arkose/FunCaptcha puzzle, a device
///   confirmation, a forced password reset). Those can only be answered in a real browser, so the
///   result says so and the caller falls back to the browser sign-in window.</description></item>
/// </list>
/// </summary>
public static class RobloxAuthService
{
    private const string LoginUrl = "https://auth.roblox.com/v2/login";
    private const string TwoStepBase = "https://twostepverification.roblox.com/v1";

    /// <summary>How a login attempt ended.</summary>
    public enum LoginOutcome
    {
        /// <summary>Signed in; <see cref="LoginResult.Cookie"/> holds the session.</summary>
        Success,
        /// <summary>Roblox rejected the username/password pair.</summary>
        InvalidCredentials,
        /// <summary>A 6-digit code is needed; see <see cref="LoginResult.TwoStep"/>.</summary>
        TwoStepRequired,
        /// <summary>Roblox wants a puzzle/confirmation that only a browser can show.</summary>
        ChallengeRequired,
        /// <summary>Too many attempts from this connection — Roblox is throttling.</summary>
        RateLimited,
        /// <summary>Anything else (network down, unexpected response shape).</summary>
        Failed
    }

    /// <summary>Everything needed to answer a two-step prompt and resume the same login.</summary>
    /// <param name="UserId">Account the challenge belongs to.</param>
    /// <param name="MediaType">Where the code comes from — <c>authenticator</c> or <c>email</c>.</param>
    /// <param name="ChallengeId">Ticket identifying this two-step attempt.</param>
    /// <param name="HeaderChallengeId">
    /// Outer challenge id from the <c>rblx-challenge-id</c> header, when Roblox used the newer
    /// header-driven flow. Null for the older body-driven flow.
    /// </param>
    public sealed record TwoStepChallenge(long UserId, string MediaType, string ChallengeId, string? HeaderChallengeId)
    {
        /// <summary>Human wording for the dialog, e.g. "your authenticator app".</summary>
        public string SourceText => MediaType.ToLowerInvariant() switch
        {
            "email" => L.T("Auth.Source.Email"),
            "sms" => L.T("Auth.Source.Sms"),
            _ => L.T("Auth.Source.Authenticator"),
        };
    }

    /// <summary>Outcome of one login or two-step step.</summary>
    public sealed record LoginResult(LoginOutcome Outcome, string Message, string? Cookie = null, TwoStepChallenge? TwoStep = null);

    // ---------------------------------------------------------------
    //  Public entry points
    // ---------------------------------------------------------------

    /// <summary>Attempts a username/password sign-in.</summary>
    public static async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0) return new(LoginOutcome.Failed, L.T("Auth.NeedUsername"));
        if (string.IsNullOrEmpty(password)) return new(LoginOutcome.Failed, L.T("Auth.NeedPassword"));

        using var http = BuildClient();
        return await PostLoginAsync(http, username, password, challengeHeaders: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a two-step prompt and finishes the login the same credentials started. Roblox
    /// verifies the code first and then wants the original login replayed with proof of that
    /// verification attached, which is why the password is needed a second time.
    /// </summary>
    public static async Task<LoginResult> CompleteTwoStepAsync(string username, string password,
        TwoStepChallenge challenge, string code, CancellationToken ct = default)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length == 0) return new(LoginOutcome.TwoStepRequired, L.T("Auth.NeedCode"), TwoStep: challenge);

        using var http = BuildClient();

        string? csrf = await FetchCsrfAsync(http, ct).ConfigureAwait(false);
        var (token, error) = await VerifyCodeAsync(http, csrf, challenge, code, ct).ConfigureAwait(false);
        if (token == null)
            return new(LoginOutcome.TwoStepRequired, error, TwoStep: challenge);

        // Proof of the solved challenge rides along as headers on a replay of the original login.
        string metadata = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            verificationToken = token,
            rememberDevice = false,
            challengeId = challenge.ChallengeId,
            actionType = "Login"
        })));

        var headers = new Dictionary<string, string>
        {
            ["rblx-challenge-id"] = challenge.HeaderChallengeId ?? challenge.ChallengeId,
            ["rblx-challenge-type"] = "twostepverification",
            ["rblx-challenge-metadata"] = metadata
        };

        return await PostLoginAsync(http, username, password, headers, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------
    //  Login request
    // ---------------------------------------------------------------

    private static async Task<LoginResult> PostLoginAsync(HttpClient http, string username, string password,
        IDictionary<string, string>? challengeHeaders, CancellationToken ct)
    {
        string? csrf = await FetchCsrfAsync(http, ct).ConfigureAwait(false);
        string lastError = L.T("Auth.NoAnswer");

        // Up to three attempts: the CSRF token rotates freely and a stale one comes back as a
        // 403 carrying the replacement, which would otherwise read as a failed login.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, LoginUrl)
                {
                    Content = JsonBody(new { ctype = "Username", cvalue = username, password })
                };
                AddWebHeaders(req, csrf);
                if (challengeHeaders != null)
                {
                    foreach (var kv in challengeHeaders)
                        req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }

                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Forbidden && !HasChallengeHeader(resp))
                {
                    string? fresh = Header(resp, "x-csrf-token");
                    if (!string.IsNullOrEmpty(fresh) && fresh != csrf)
                    {
                        csrf = fresh;
                        lastError = L.T("Auth.NoAnswer");
                        continue;   // replay with the token Roblox just handed us
                    }
                }

                if ((int)resp.StatusCode == 429)
                {
                    return new(LoginOutcome.RateLimited, L.T("Auth.RateLimited"));
                }

                // An interactive challenge (captcha, device confirmation) — only a browser can answer it.
                if (HasChallengeHeader(resp))
                {
                    string type = Header(resp, "rblx-challenge-type") ?? "";
                    if (type.Equals("twostepverification", StringComparison.OrdinalIgnoreCase))
                    {
                        var fromHeader = ParseHeaderTwoStep(resp);
                        if (fromHeader != null)
                        {
                            return new(LoginOutcome.TwoStepRequired,
                                L.T("Auth.EnterCode", fromHeader.SourceText), TwoStep: fromHeader);
                        }
                    }

                    // The type only (never the metadata): it tells a captcha from a browser check in a report.
                    DiagnosticsService.Log("auth", $"Roblox asked for a '{(type.Length > 0 ? type : "unknown")}' check before signing in (HTTP {(int)resp.StatusCode})");
                    return new(LoginOutcome.ChallengeRequired, L.T("Auth.Challenge"));
                }

                if (resp.IsSuccessStatusCode)
                {
                    // Two-step, older shape: HTTP 200 with a ticket instead of a session cookie.
                    var fromBody = ParseBodyTwoStep(body);
                    if (fromBody != null)
                    {
                        return new(LoginOutcome.TwoStepRequired,
                            L.T("Auth.EnterCode", fromBody.SourceText), TwoStep: fromBody);
                    }

                    string? cookie = ExtractRoblosecurity(resp);
                    if (cookie != null) return new(LoginOutcome.Success, L.T("Auth.SignedIn"), cookie);

                    lastError = L.T("Auth.NoCookie");
                    break;
                }

                string? apiMessage = FirstApiError(body);
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                {
                    return new(LoginOutcome.InvalidCredentials,
                        apiMessage ?? L.T("Auth.Rejected"));
                }

                lastError = apiMessage ?? L.T("Auth.Http", (int)resp.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = ex.Message; }
        }

        return new(LoginOutcome.Failed, lastError);
    }

    // ---------------------------------------------------------------
    //  Two-step verification
    // ---------------------------------------------------------------

    /// <summary>
    /// Submits the 6-digit code. Returns the verification token Roblox issues, or a message
    /// explaining why the code was refused.
    /// </summary>
    private static async Task<(string? token, string error)> VerifyCodeAsync(HttpClient http, string? csrf,
        TwoStepChallenge challenge, string code, CancellationToken ct)
    {
        string url = $"{TwoStepBase}/users/{challenge.UserId}/challenges/{challenge.MediaType}/verify";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonBody(new { challengeId = challenge.ChallengeId, actionType = "Login", code })
                };
                AddWebHeaders(req, csrf);

                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    string? fresh = Header(resp, "x-csrf-token");
                    if (!string.IsNullOrEmpty(fresh) && fresh != csrf) { csrf = fresh; continue; }
                }

                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("verificationToken", out var t) && t.GetString() is { Length: > 0 } tok)
                        return (tok, "");
                    return (null, L.T("Auth.NoToken"));
                }

                return (null, FirstApiError(body) ?? L.T("Auth.CodeRejected"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (null, ex.Message); }
        }

        return (null, L.T("Auth.NoAnswer"));
    }

    /// <summary>Two-step details from the older body shape (<c>twoStepVerificationData</c>).</summary>
    private static TwoStepChallenge? ParseBodyTwoStep(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("twoStepVerificationData", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;

            string ticket = data.TryGetProperty("ticket", out var t) ? t.GetString() ?? "" : "";
            string media = data.TryGetProperty("mediaType", out var m) ? m.GetString() ?? "" : "";
            long userId = root.TryGetProperty("user", out var u)
                       && u.TryGetProperty("id", out var uid)
                       && uid.TryGetInt64(out long v) ? v : 0;

            if (ticket.Length == 0 || userId == 0) return null;
            return new TwoStepChallenge(userId, NormalizeMedia(media), ticket, null);
        }
        catch { return null; }
    }

    /// <summary>
    /// Two-step details from the newer header shape. The metadata blob carries the user and the
    /// challenge id but not always which factor is configured, so the media type falls back to the
    /// far more common authenticator app.
    /// </summary>
    private static TwoStepChallenge? ParseHeaderTwoStep(HttpResponseMessage resp)
    {
        try
        {
            string? headerId = Header(resp, "rblx-challenge-id");
            string? raw = Header(resp, "rblx-challenge-metadata");
            if (string.IsNullOrEmpty(raw)) return null;

            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(raw)));
            var root = doc.RootElement;

            string challengeId = root.TryGetProperty("challengeId", out var c) ? c.GetString() ?? "" : "";
            string userText = root.TryGetProperty("userId", out var u)
                ? (u.ValueKind == JsonValueKind.Number ? u.GetInt64().ToString() : u.GetString() ?? "")
                : "";

            if (challengeId.Length == 0 || !long.TryParse(userText, out long userId)) return null;

            string media = root.TryGetProperty("mediaType", out var m) ? m.GetString() ?? "" : "";
            return new TwoStepChallenge(userId, NormalizeMedia(media), challengeId, headerId);
        }
        catch { return null; }
    }

    /// <summary>Roblox spells these "Authenticator"/"Email"; the verify route wants them lowercase.</summary>
    private static string NormalizeMedia(string? mediaType)
    {
        string m = (mediaType ?? "").Trim().ToLowerInvariant();
        return m switch
        {
            "email" => "email",
            "sms" => "sms",
            "securitykey" => "securityKey",
            _ => "authenticator"
        };
    }

    // ---------------------------------------------------------------
    //  Plumbing
    // ---------------------------------------------------------------

    private static HttpClient BuildClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,          // the session cookie is read straight off the response
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var s = SettingsService.Current;
        if (s.EnableProxy && !string.IsNullOrWhiteSpace(s.ProxyAddress))
        {
            var proxy = RobloxApi.TryBuildProxy(s.ProxyAddress, s.ProxyUsername, s.ProxyPassword);
            if (proxy != null) { handler.Proxy = proxy; handler.UseProxy = true; }
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        // The login endpoint is browser-facing and rejects obviously non-browser callers.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        return client;
    }

    private static void AddWebHeaders(HttpRequestMessage req, string? csrf)
    {
        if (!string.IsNullOrEmpty(csrf)) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    /// <summary>
    /// The login endpoint doubles as the CSRF source: an unauthenticated POST is answered with
    /// 403 plus a fresh <c>x-csrf-token</c>, which every following request replays.
    /// </summary>
    private static async Task<string?> FetchCsrfAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, LoginUrl) { Content = JsonBody(new { }) };
            AddWebHeaders(req, null);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return Header(resp, "x-csrf-token");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static StringContent JsonBody(object o)
    {
        var content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string? Header(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool HasChallengeHeader(HttpResponseMessage resp)
        => !string.IsNullOrEmpty(Header(resp, "rblx-challenge-id"))
        || !string.IsNullOrEmpty(Header(resp, "rblx-challenge-type"));

    private static readonly Regex CookiePattern = new(@"\.ROBLOSECURITY=([^;]+)", RegexOptions.Compiled);

    /// <summary>Pulls the session cookie out of the response's Set-Cookie headers.</summary>
    private static string? ExtractRoblosecurity(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("set-cookie", out var cookies)) return null;
        foreach (string raw in cookies)
        {
            var m = CookiePattern.Match(raw);
            if (!m.Success) continue;
            string value = m.Groups[1].Value.Trim();
            // A logout/placeholder cookie carries no warning prefix — ignore those.
            if (value.Length > 0 && value.Contains("WARNING", StringComparison.Ordinal)) return value;
        }
        return null;
    }

    /// <summary>First human-readable message out of Roblox's <c>{"errors":[…]}</c> envelope.</summary>
    private static string? FirstApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var e in errors.EnumerateArray())
            {
                if (e.TryGetProperty("userFacingMessage", out var ufm) && ufm.GetString() is { Length: > 0 } friendly)
                    return friendly;
                if (e.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } m)
                    return m;
            }
        }
        catch { }
        return null;
    }
}
