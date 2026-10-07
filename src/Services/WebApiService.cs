using System.Net;
using System.Text;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Local HTTP/REST control server bound to 127.0.0.1 only (never a public interface, so it needs
/// no admin/netsh URL ACL). Every request must carry the configured bearer token — an empty token
/// in settings disables the whole surface. Lets external scripts/tools list accounts, launch, close,
/// read presence and (token-gated) pull the .ROBLOSECURITY cookie.
///
/// Endpoints:
///   GET  /ping                                  health, no auth
///   GET  /accounts                              list (no cookies)
///   POST /launch?account=&amp;placeId=&amp;jobId=       launch one account (instead of jobId: link= a game /
///                                               private-server / share link, or followUserId=)
///   POST /preset?name=                          start a launch preset (runs in the background)
///   POST /close?account=                        close that account's tracked clients
///   GET  /status?account=                       presence snapshot
///   GET  /cookie?account=                       .ROBLOSECURITY (sensitive; off unless enabled in Settings)
/// Auth: "Authorization: Bearer &lt;token&gt;" header or "?token=&lt;token&gt;".
/// Requests carrying an Origin header (i.e. from a web page) or a non-loopback Host are refused.
/// </summary>
public static class WebApiService
{
    private static readonly object _gate = new();
    private static HttpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static Func<IReadOnlyList<Account>>? _accounts;

    /// <summary>Wires the live account-list provider (usually vm.Store.Accounts snapshotted per call).</summary>
    public static void Init(Func<IReadOnlyList<Account>> accountsProvider) => _accounts = accountsProvider;

    /// <summary>Starts or stops the server to match current settings (idempotent).</summary>
    public static void Apply()
    {
        var s = SettingsService.Current;
        if (s.WebApiEnabled && !string.IsNullOrWhiteSpace(s.WebApiToken)) Start();
        else Stop();
    }

    public static void Start()
    {
        lock (_gate)
        {
            if (_listener != null) return;

            var port = SettingsService.Current.WebApiPort;
            if (port is < 1 or > 65535) port = 7963;

            var listener = new HttpListener();
            // 127.0.0.1 (not localhost/+/*) keeps the binding user-scoped: no admin, no URL ACL.
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch { return; } // port taken / denied — fail silently, UI shows the toggle didn't stick

            _listener = listener;
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(listener, _cts.Token);
        }
    }

    public static void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            _cts = null;
        }
    }

    private static async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // listener stopped

            _ = HandleAsync(ctx); // fire-and-forget: one slow/bad request never blocks the accept loop
        }
    }

    private static async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";
            if (path.Length == 0) path = "/ping";

            // DNS-rebinding guard: a web page that resolves its own hostname to 127.0.0.1 would reach
            // this server with a foreign Host header. Only literal loopback names are accepted.
            if (!IsLoopbackHost(req))
            {
                await WriteJsonAsync(ctx, 403, new { error = "forbidden host" });
                return;
            }

            // Browsers attach an Origin header to cross-site requests; scripts and tools do not. No
            // legitimate client of this API is a web page, so refusing those closes the whole class of
            // drive-by requests from a site the user happens to have open.
            if (!string.IsNullOrEmpty(req.Headers["Origin"]))
            {
                await WriteJsonAsync(ctx, 403, new { error = "browser requests are not allowed" });
                return;
            }

            // Health check is the only unauthenticated route.
            if (path == "/ping")
            {
                await WriteJsonAsync(ctx, 200, new { ok = true, app = "Roblox Account Manager", version = AppInfo.Number });
                return;
            }

            if (IsThrottled())
            {
                await WriteJsonAsync(ctx, 429, new { error = "too many failed attempts" });
                return;
            }

            if (!IsAuthorized(req))
            {
                NoteFailure();
                await WriteJsonAsync(ctx, 401, new { error = "unauthorized" });
                return;
            }

            if (LockService.IsLocked && path != "/accounts" && path != "/status")
            {
                await WriteJsonAsync(ctx, 423, new { error = "the manager is locked" });
                return;
            }

            bool isPost = req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
            switch (path)
            {
                case "/accounts": await HandleAccountsAsync(ctx); break;
                case "/status":   await HandleStatusAsync(ctx);   break;
                case "/launch" when isPost: await HandleLaunchAsync(ctx); break;
                case "/preset" when isPost: await HandlePresetAsync(ctx); break;
                case "/close" when isPost:  await HandleCloseAsync(ctx);  break;
                case "/launch" or "/close" or "/preset":
                    await WriteJsonAsync(ctx, 405, new { error = "use POST" });
                    break;
                case "/cookie":
                    if (SettingsService.Current.WebApiAllowCookieRead) await HandleCookieAsync(ctx);
                    else await WriteJsonAsync(ctx, 403, new { error = "cookie access is disabled in Settings" });
                    break;
                default: await WriteJsonAsync(ctx, 404, new { error = "not found" }); break;
            }
        }
        catch
        {
            try { await WriteJsonAsync(ctx, 500, new { error = "internal" }); } catch { }
        }
    }

    private static bool IsLoopbackHost(HttpListenerRequest req)
    {
        string host = (req.UserHostName ?? "").ToLowerInvariant();
        int colon = host.LastIndexOf(':');
        if (colon > 0 && !host.EndsWith("]")) host = host[..colon];
        return host is "127.0.0.1" or "localhost" or "[::1]";
    }

    // ---- brute-force brake ----
    private static readonly Queue<DateTime> _failures = new();

    private static void NoteFailure()
    {
        lock (_failures)
        {
            _failures.Enqueue(DateTime.UtcNow);
            while (_failures.Count > 50) _failures.Dequeue();
        }
        AuditLogService.Log(AuditLogService.Category.Security, "Local API: request with a wrong token");
    }

    /// <summary>More than 10 bad tokens in a minute locks the API for the rest of that minute.</summary>
    private static bool IsThrottled()
    {
        lock (_failures)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (_failures.Count > 0 && _failures.Peek() < cutoff) _failures.Dequeue();
            return _failures.Count >= 10;
        }
    }

    // ---- auth ----

    private static bool IsAuthorized(HttpListenerRequest req)
    {
        var token = SettingsService.Current.WebApiToken;
        if (string.IsNullOrWhiteSpace(token)) return false; // no token set → surface is closed

        var header = req.Headers["Authorization"];
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            if (FixedTimeEquals(header.Substring(7).Trim(), token)) return true;

        var q = req.QueryString["token"];
        return !string.IsNullOrEmpty(q) && FixedTimeEquals(q, token);
    }

    /// <summary>Length-independent constant-time compare so the token can't be timing-probed.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(ba),
            System.Security.Cryptography.SHA256.HashData(bb));
    }

    // ---- handlers ----

    private static async Task HandleAccountsAsync(HttpListenerContext ctx)
    {
        var list = (_accounts?.Invoke() ?? Array.Empty<Account>())
            .Select(a => new
            {
                userId = a.UserId,
                username = a.Username,
                displayName = a.DisplayName,
                alias = a.Alias,
                group = a.Group,
                presence = a.Presence,
                robux = a.Robux,
                valid = a.IsValid,
                health = a.Health,
                running = a.RunningClients
            });
        await WriteJsonAsync(ctx, 200, list);
    }

    private static async Task HandleLaunchAsync(HttpListenerContext ctx)
    {
        var acc = Resolve(ctx.Request.QueryString["account"]);
        if (acc == null) { await WriteJsonAsync(ctx, 404, new { error = "account not found" }); return; }

        var q = ctx.Request.QueryString;
        long.TryParse(q["placeId"], out var placeId);
        long.TryParse(q["followUserId"], out var followUserId);
        string? link = q["link"];
        if (placeId == 0 && followUserId <= 0 && string.IsNullOrWhiteSpace(link)) placeId = SettingsService.Current.DefaultPlaceId;

        // Same destination rules as the launch bar: a link or Job ID goes through the shared resolver.
        JoinTarget target;
        if (followUserId > 0) target = new JoinTarget(0, FollowUserId: followUserId);
        else
        {
            var resolved = await JoinTargetResolver.FromServerInputAsync(!string.IsNullOrWhiteSpace(link) ? link : q["jobId"], placeId, () => acc.Cookie);
            if (resolved.Target == null) { await WriteJsonAsync(ctx, 400, new { ok = false, message = resolved.Error }); return; }
            target = resolved.Target;
        }

        var result = await LauncherService.LaunchAsync(acc, target);
        await WriteJsonAsync(ctx, result.Success ? 200 : 400, new { ok = result.Success, message = result.Message });
    }

    private static async Task HandlePresetAsync(HttpListenerContext ctx)
    {
        var preset = PresetService.Find(ctx.Request.QueryString["name"] ?? "");
        if (preset == null) { await WriteJsonAsync(ctx, 404, new { error = "preset not found" }); return; }

        // A preset can take minutes (delays between accounts); answer now instead of holding the request.
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await PresetService.LaunchAsync(preset);
                DiagnosticsService.Log("webapi", $"Preset '{preset.Name}': {r.Launched} launched, {r.Failed} failed");
            }
            catch (Exception ex) { DiagnosticsService.Warn("webapi", $"Preset '{preset.Name}' failed", ex); }
        });
        await WriteJsonAsync(ctx, 202, new { ok = true, started = preset.Name, accounts = preset.Aliases.Count });
    }

    private static async Task HandleCloseAsync(HttpListenerContext ctx)
    {
        var acc = Resolve(ctx.Request.QueryString["account"]);
        if (acc == null) { await WriteJsonAsync(ctx, 404, new { error = "account not found" }); return; }

        int closed = await Task.Run(() => InstanceControlService.CloseFor(acc.UserId));
        await WriteJsonAsync(ctx, 200, new { ok = true, closed });
    }

    private static async Task HandleStatusAsync(HttpListenerContext ctx)
    {
        var acc = Resolve(ctx.Request.QueryString["account"]);
        if (acc == null) { await WriteJsonAsync(ctx, 404, new { error = "account not found" }); return; }

        await WriteJsonAsync(ctx, 200, new
        {
            userId = acc.UserId,
            username = acc.Username,
            presence = acc.Presence,
            robux = acc.Robux,
            valid = acc.IsValid,
            running = ProcessRegistry.ForUser(acc.UserId).Any()
        });
    }

    private static async Task HandleCookieAsync(HttpListenerContext ctx)
    {
        var acc = Resolve(ctx.Request.QueryString["account"]);
        if (acc == null) { await WriteJsonAsync(ctx, 404, new { error = "account not found" }); return; }
        AuditLogService.Log(AuditLogService.Category.Cookie, $"Local API: cookie read for {acc.Username} (userId {acc.UserId})");
        await WriteJsonAsync(ctx, 200, new { userId = acc.UserId, cookie = acc.Cookie });
    }

    // ---- helpers ----

    /// <summary>Resolves an account by numeric userId, then alias, then username (all case-insensitive).</summary>
    private static Account? Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var list = _accounts?.Invoke() ?? Array.Empty<Account>();

        if (long.TryParse(key, out var id))
        {
            var byId = list.FirstOrDefault(a => a.UserId == id);
            if (byId != null) return byId;
        }
        return list.FirstOrDefault(a => string.Equals(a.Alias,    key, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(a => string.Equals(a.Username, key, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();   // closes the stream AND releases the HttpListener response
    }
}
