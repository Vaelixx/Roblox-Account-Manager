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
/// Opens an account in a real browser, logged in. A Chromium-based browser (Edge, Chrome or Brave)
/// is launched with a per-account profile and remote-debugging enabled; the .ROBLOSECURITY cookie is
/// injected over the DevTools protocol, then the tab is navigated to roblox.com — landing signed in.
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

    /// <summary>Launches the private Chromium build signed in as this account.</summary>
    public static async Task<OpenResult> OpenLoggedInAsync(Account acc)
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
            Process.Start(psi);

            string? wsUrl = await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(15));
            if (wsUrl == null)
                return new(false, "Chromium started but the debugger didn't respond in time.");

            await InjectCookieAndNavigateAsync(wsUrl, acc.Cookie);
            return new(true, $"Opened {acc.DisplayNameOrUser} in Chromium.");
        }
        catch (Exception ex)
        {
            return new(false, $"Couldn't open Chromium: {ex.Message}");
        }
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

    private static async Task InjectCookieAndNavigateAsync(string wsUrl, string cookie)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        await SendAsync(socket, 1, "Network.enable", new { });
        await SendAsync(socket, 2, "Network.setCookie", new
        {
            name = ".ROBLOSECURITY",
            value = cookie,
            domain = ".roblox.com",
            path = "/",
            secure = true,
            httpOnly = true,
            expires = 4102444800.0 // year 2100
        });
        await SendAsync(socket, 3, "Page.navigate", new { url = "https://www.roblox.com/home" });

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
