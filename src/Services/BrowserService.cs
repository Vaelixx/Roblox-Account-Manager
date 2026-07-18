using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Opens an account in a real browser, logged in. The private CloakBrowser build is launched with a
/// per-account profile and remote-debugging enabled; the .ROBLOSECURITY cookie is injected over the
/// DevTools protocol, then the tab is navigated to roblox.com — landing signed in.
/// </summary>
public static class BrowserService
{
    /// <summary>Public profile page in the user's default browser (not logged in).</summary>
    public static void OpenProfile(Account acc)
    {
        if (acc.UserId <= 0) return;
        try
        {
            Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{acc.UserId}/profile") { UseShellExecute = true });
        }
        catch { }
    }

    public record OpenResult(bool Success, string Message);

    /// <summary>Launches the private CloakBrowser build signed in as this account. When
    /// <paramref name="injectJs"/> is set, the snippet is evaluated in the page once it has settled
    /// (the "Inject" power-tool — e.g. a bookmarklet run in the authenticated session).</summary>
    public static async Task<OpenResult> OpenLoggedInAsync(Account acc, string? injectJs = null)
    {
        if (string.IsNullOrEmpty(acc.Cookie))
            return new(false, "This account has no cookie.");

        if (!ChromiumService.IsInstalled)
            return new(false, "no-chromium");

        try
        {
            string profileDir = Paths.InData(Path.Combine("browser", acc.UserId > 0 ? acc.UserId.ToString() : "acct"));
            Directory.CreateDirectory(profileDir);

            int port = FreePort();
            var psi = new ProcessStartInfo(ChromiumService.ChromePath)
            {
                UseShellExecute = false,
                Arguments = $"--user-data-dir=\"{profileDir}\" --remote-debugging-port={port} "
                          + "--no-first-run --no-default-browser-check --new-window about:blank"
            };
            var proc = Process.Start(psi);

            // Privacy: the profile must never outlive the browser session. The moment the
            // browser closes, the whole profile folder (cookie DB, site storage, cache) is
            // wiped so nothing readable stays on disk.
            if (proc != null)
            {
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) => { WipeProfileWithRetry(profileDir); proc.Dispose(); };
                }
                catch { /* exit hook is best-effort; startup/exit cleanup still covers it */ }
            }

            string? wsUrl = await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(15));
            if (wsUrl == null)
                return new(false, "CloakBrowser started but the debugger didn't respond in time.");

            await InjectCookieAndNavigateAsync(wsUrl, acc.Cookie, injectJs);
            string what = string.IsNullOrWhiteSpace(injectJs) ? "Opened" : "Injected script into";
            return new(true, $"{what} {acc.DisplayNameOrUser} in CloakBrowser.");
        }
        catch (Exception ex)
        {
            return new(false, $"Couldn't open CloakBrowser: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes leftover app-browser profiles under data/browser. Runs at app start and exit so
    /// no cookie/site data from a previous session stays readable on disk (crash leftovers and
    /// profiles written by older versions that still used persistent cookies). A profile whose
    /// browser is still running keeps its files locked — it is skipped and caught next start.
    /// </summary>
    public static void CleanupLeftoverProfiles()
    {
        try
        {
            string root = Paths.InData("browser");
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.GetDirectories(root))
            {
                try { Directory.Delete(dir, true); }
                catch { WipeSensitiveFiles(dir); }
            }
        }
        catch { }
    }

    private static void WipeProfileWithRetry(string dir)
    {
        _ = Task.Run(async () =>
        {
            // The browser can hold file locks for a moment after its main process exits.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    Directory.Delete(dir, true);
                    return;
                }
                catch { await Task.Delay(500); }
            }
            WipeSensitiveFiles(dir); // last resort: at least nothing credential-bearing stays
        });
    }

    /// <summary>Best-effort wipe of everything credential-bearing inside a (possibly locked) profile.</summary>
    private static void WipeSensitiveFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            string[] sensitive = { "Cookies", "Login Data", "Web Data", "History",
                                   "Network Persistent State", "TransportSecurity", "Trust Tokens" };
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (sensitive.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            foreach (string sub in new[] { "Sessions", "Session Storage", "Local Storage", "IndexedDB" })
            {
                foreach (string d in Directory.EnumerateDirectories(dir, sub, SearchOption.AllDirectories).ToList())
                {
                    try { Directory.Delete(d, true); } catch { }
                }
            }
        }
        catch { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<string?> WaitForPageSocketAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json");
                using var doc = JsonDocument.Parse(json);
                foreach (var target in doc.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var ty) && ty.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out var ws))
                        return ws.GetString();
                }
            }
            catch { }
            await Task.Delay(250);
        }
        return null;
    }

    private static async Task InjectCookieAndNavigateAsync(string wsUrl, string cookie, string? injectJs = null)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        await SendAsync(socket, 1, "Network.enable", new { });
        // No "expires" on purpose: that makes it a session cookie, which lives in browser
        // memory only and dies with the window — it is never persisted into the profile's
        // cookie database where it would sit readable on disk.
        await SendAsync(socket, 2, "Network.setCookie", new
        {
            name = ".ROBLOSECURITY",
            value = cookie,
            domain = ".roblox.com",
            path = "/",
            secure = true,
            httpOnly = true
        });
        await SendAsync(socket, 3, "Page.navigate", new { url = "https://www.roblox.com/home" });

        if (!string.IsNullOrWhiteSpace(injectJs))
        {
            // Let the page load before running the snippet, then evaluate it in the top frame.
            await SendAsync(socket, 4, "Runtime.enable", new { });
            await Task.Delay(2500);
            await SendAsync(socket, 5, "Runtime.evaluate", new
            {
                expression = injectJs,
                userGesture = true,
                awaitPromise = false
            });
        }

        // Give the commands a moment to be processed before we drop the socket.
        await Task.Delay(400);
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }

    private static async Task SendAsync(ClientWebSocket socket, int id, string method, object @params)
    {
        string payload = JsonSerializer.Serialize(new { id, method, @params });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        await Task.Delay(120); // small gap so commands land in order
    }
}
