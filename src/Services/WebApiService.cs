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
///   POST /launch?account=&amp;placeId=&amp;jobId=       launch one account
///   POST /close?account=                        kill that account's tracked clients
///   GET  /status?account=                       presence snapshot
///   GET  /cookie?account=                       .ROBLOSECURITY (sensitive)
/// Auth: "Authorization: Bearer &lt;token&gt;" header or "?token=&lt;token&gt;".
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

            // Health check is the only unauthenticated route.
            if (path == "/ping")
            {
                await WriteJsonAsync(ctx, 200, new { ok = true, app = "Roblox Account Manager", version = AppInfo.Number });
                return;
            }

            if (!IsAuthorized(req))
            {
                await WriteJsonAsync(ctx, 401, new { error = "unauthorized" });
                return;
            }

            switch (path)
            {
                case "/accounts": await HandleAccountsAsync(ctx); break;
                case "/launch":   await HandleLaunchAsync(ctx);   break;
                case "/close":    await HandleCloseAsync(ctx);    break;
                case "/status":   await HandleStatusAsync(ctx);   break;
                case "/cookie":   await HandleCookieAsync(ctx);   break;
                default:          await WriteJsonAsync(ctx, 404, new { error = "not found" }); break;
            }
        }
        catch
        {
            try { await WriteJsonAsync(ctx, 500, new { error = "internal" }); } catch { }
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
                valid = a.IsValid
            });
        await WriteJsonAsync(ctx, 200, list);
    }

    private static async Task HandleLaunchAsync(HttpListenerContext ctx)
    {
        var acc = Resolve(ctx.Request.QueryString["account"]);
        if (acc == null) { await WriteJsonAsync(ctx, 404, new { error = "account not found" }); return; }

        long.TryParse(ctx.Request.QueryString["placeId"], out var placeId);
        if (placeId == 0) placeId = SettingsService.Current.DefaultPlaceId;
        var jobId = ctx.Request.QueryString["jobId"];

        var result = await LauncherService.LaunchAsync(acc, placeId, string.IsNullOrWhiteSpace(jobId) ? null : jobId);
        await WriteJsonAsync(ctx, result.Success ? 200 : 400, new { ok = result.Success, message = result.Message });
    }

    private static async Task HandleCloseAsync(HttpListenerContext ctx)
    {
        var acc = Resolve(ctx.Request.QueryString["account"]);
        if (acc == null) { await WriteJsonAsync(ctx, 404, new { error = "account not found" }); return; }

        int killed = 0;
        foreach (var t in ProcessRegistry.ForUser(acc.UserId).ToList())
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(t.Pid);
                p.Kill();
                ProcessRegistry.Forget(t.Pid);
                killed++;
            }
            catch { }
        }
        await WriteJsonAsync(ctx, 200, new { ok = true, closed = killed });
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
