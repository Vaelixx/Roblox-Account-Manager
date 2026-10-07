using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Opens Roblox in a real browser — signed in as an account, or on the login page to capture a
/// fresh session — and drives it over the Chrome DevTools protocol.
///
/// Any Chromium works: the private CloakBrowser build when it is installed, otherwise the Microsoft
/// Edge every Windows 10/11 PC already has, or Google Chrome. The user's own browser profile is never
/// touched: every window gets a brand-new profile folder that is deleted the moment it closes, and the
/// session cookie is injected as a session-only cookie so it is never written into that profile.
/// </summary>
public static class BrowserService
{
    public const string EngineAuto = "Auto";
    public static readonly string[] Engines = { EngineAuto, "CloakBrowser", "Edge", "Chrome" };

    public sealed record BrowserInfo(string Engine, string ExePath);

    /// <summary>Public profile page in the user's default browser (not signed in).</summary>
    public static void OpenProfile(long userId)
    {
        if (userId <= 0) return;
        OpenUrl($"https://www.roblox.com/users/{userId}/profile");
    }

    public static void OpenProfile(Account acc) => OpenProfile(acc.UserId);

    /// <summary>Opens an https URL in the default browser. Anything else is refused.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { using (Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })) { } }
        catch (Exception ex) { DiagnosticsService.Warn("browser", "Could not open the default browser", ex); }
    }

    // ---------------------------------------------------------------
    //  Browser discovery
    // ---------------------------------------------------------------

    /// <summary>The browser a window will open in, honouring the Browser setting; null when none is available.</summary>
    public static BrowserInfo? Resolve()
    {
        string pref = SettingsService.Current.BrowserEngine;
        if (pref != EngineAuto)
        {
            var chosen = Find(pref);
            if (chosen != null) return chosen;
        }
        return Find("CloakBrowser") ?? Find("Edge") ?? Find("Chrome");
    }

    /// <summary>Installed browsers, for the settings picker.</summary>
    public static IReadOnlyList<BrowserInfo> Available()
        => new[] { Find("CloakBrowser"), Find("Edge"), Find("Chrome") }.Where(b => b != null).Cast<BrowserInfo>().ToList();

    public static BrowserInfo? Find(string engine)
    {
        string? path = engine switch
        {
            "CloakBrowser" => ChromiumService.IsInstalled ? ChromiumService.ChromePath : null,
            "Edge" => FirstExisting(AppPath("msedge.exe"),
                        Path.Combine(ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
                        Path.Combine(ProgramFiles, @"Microsoft\Edge\Application\msedge.exe")),
            "Chrome" => FirstExisting(AppPath("chrome.exe"),
                        Path.Combine(ProgramFiles, @"Google\Chrome\Application\chrome.exe"),
                        Path.Combine(ProgramFilesX86, @"Google\Chrome\Application\chrome.exe"),
                        Path.Combine(LocalAppData, @"Google\Chrome\Application\chrome.exe")),
            _ => null,
        };
        return string.IsNullOrEmpty(path) ? null : new BrowserInfo(engine, path);
    }

    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? FirstExisting(params string?[] paths)
    {
        foreach (var p in paths)
        {
            try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p; }
            catch { }
        }
        return null;
    }

    /// <summary>Registered install path from the App Paths key (per-user first, then machine-wide).</summary>
    private static string? AppPath(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (key?.GetValue(null) is string value && value.Length > 0) return value.Trim('"');
            }
            catch { }
        }
        return null;
    }

    // ---------------------------------------------------------------
    //  Open signed in
    // ---------------------------------------------------------------

    public record OpenResult(bool Success, string Message, bool NoBrowser = false);

    /// <summary>
    /// Opens a browser window signed in as this account. When <paramref name="injectJs"/> is set,
    /// the snippet runs in the page once it has loaded.
    /// </summary>
    public static async Task<OpenResult> OpenLoggedInAsync(Account acc, string? injectJs = null)
    {
        if (LockService.IsLocked) return new(false, L.T("Lock.Blocked"));
        if (string.IsNullOrEmpty(acc.Cookie)) return new(false, L.T("Launch.NoCookie"));

        var browser = Resolve();
        if (browser == null) return new(false, L.T("Browser.NoneFound"), NoBrowser: true);

        try
        {
            // A fresh folder per window: two windows for the same account must not share a profile,
            // or closing one would wipe the other's files while it is still running.
            string profileDir = NewProfileDir(acc.UserId > 0 ? acc.UserId.ToString() : "account");
            int port = FreePort();

            var args = BaseArguments(profileDir, port);
            string? proxy = RobloxApi.ProxyServerArgument(acc.ProxyUrl);
            if (proxy != null) args.Add($"--proxy-server={proxy}");
            args.Add("--window-size=1280,860");
            args.Add("about:blank");

            Process? proc;
            try { proc = Start(browser, args); }
            catch (Exception ex)
            {
                DiagnosticsService.Warn("browser", $"{browser.Engine} could not be started", ex);
                WipeProfileWithRetry(profileDir);
                return new(false, L.T("Browser.LaunchFailed", browser.Engine, ex.Message));
            }

            string? wsUrl = await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(20));
            if (wsUrl == null)
            {
                await CloseBrowserAsync(port, proc);
                proc?.Dispose();
                WipeProfileWithRetry(profileDir);
                return new(false, L.T("Browser.DebuggerTimeout", browser.Engine));
            }

            // The profile must never outlive its window. Watched through DevTools, not the launcher
            // process: that one may exit at once while the window stays open.
            _ = CleanupWhenClosedAsync(port, proc, profileDir);

            await InjectCookieAndNavigateAsync(wsUrl, acc.Cookie, injectJs);
            return new(true, string.IsNullOrWhiteSpace(injectJs)
                ? L.T("Browser.Opened", acc.DisplayNameOrUser, browser.Engine)
                : L.T("Browser.Injected", acc.DisplayNameOrUser));
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("browser", "Opening a signed-in window failed", ex);
            return new(false, L.T("Browser.OpenFailed", ex.Message));
        }
    }

    // ---------------------------------------------------------------
    //  Browser sign-in
    // ---------------------------------------------------------------

    /// <summary>Result of a browser sign-in: the captured session cookie, or why it did not happen.</summary>
    public sealed record LoginCapture(bool Success, string Message, string? Cookie, bool NoBrowser = false);

    /// <summary>
    /// Opens the real Roblox login page in a clean app-style window and waits for the user to sign in,
    /// then reads the new .ROBLOSECURITY cookie over the DevTools protocol.
    ///
    /// This is the sign-in that keeps working whatever Roblox changes about its login flow — captchas,
    /// device confirmation, passkeys, new security checks — because it is Roblox's own page in a real
    /// browser. The password never passes through this app.
    /// </summary>
    public static async Task<LoginCapture> CaptureLoginCookieAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var browser = Resolve();
        if (browser == null) return new(false, L.T("Browser.NoneFound"), null, NoBrowser: true);

        string profileDir = NewProfileDir("login");
        Process? proc = null;
        ClientWebSocket? socket = null;
        int port = 0;

        try
        {
            port = FreePort();
            var args = BaseArguments(profileDir, port);
            args.Add("--window-size=520,820");
            args.Add("--app=https://www.roblox.com/login");
            progress?.Report(L.T("Browser.Login.Opening", browser.Engine));
            try { proc = Start(browser, args); }
            catch (Exception ex)
            {
                DiagnosticsService.Warn("browser", $"{browser.Engine} could not be started for sign-in", ex);
                return new(false, L.T("Browser.LaunchFailed", browser.Engine, ex.Message), null);
            }

            // The browser-level endpoint survives every navigation the login flow performs.
            string? wsUrl = await WaitForBrowserSocketAsync(port, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            bool pageTarget = false;
            if (wsUrl == null)
            {
                wsUrl = await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                pageTarget = wsUrl != null;
            }
            if (wsUrl == null)
            {
                // A launcher that exited with an error never produced a browser; one that exited
                // cleanly may have handed off to a browser that just isn't answering.
                if (proc is { HasExited: true } && proc.ExitCode != 0)
                    return new(false, L.T("Browser.LaunchFailed", browser.Engine, $"exit code {proc.ExitCode}"), null);
                return new(false, L.T("Browser.DebuggerTimeout", browser.Engine), null);
            }

            socket = await ConnectAsync(wsUrl, ct).ConfigureAwait(false);

            int id = 0;
            string method = pageTarget ? "Network.getAllCookies" : "Storage.getCookies";
            if (pageTarget) (await CallAsync(socket, ++id, "Network.enable", new { }, ct).ConfigureAwait(false))?.Dispose();

            progress?.Report(L.T("Browser.Login.Waiting", browser.Engine));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var liveness = new BrowserLiveness();
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                // Session liveness comes from DevTools, never from the process Start() returned.
                var (reachable, pages) = await ProbeAsync(http, port).ConfigureAwait(false);
                if (liveness.Observe(reachable, pages))
                    return new(false, L.T("Browser.Login.Closed"), null);

                if (reachable && socket is not { State: WebSocketState.Open })
                {
                    // The DevTools connection dropped while the browser is still there: reconnect.
                    try { socket?.Dispose(); } catch { }
                    socket = null;
                    string? again = pageTarget
                        ? await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(5)).ConfigureAwait(false)
                        : await WaitForBrowserSocketAsync(port, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (again == null) { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
                    socket = await ConnectAsync(again, ct).ConfigureAwait(false);
                    if (pageTarget) (await CallAsync(socket, ++id, "Network.enable", new { }, ct).ConfigureAwait(false))?.Dispose();
                }

                if (socket is { State: WebSocketState.Open })
                {
                    var (cookie, unsupported) = await TryReadSessionCookieAsync(socket, ++id, method, ct).ConfigureAwait(false);
                    if (cookie != null)
                    {
                        progress?.Report(L.T("Browser.Login.Captured"));
                        return new(true, "", cookie);
                    }

                    // Some builds do not expose Storage.getCookies on the browser target; switch to a
                    // page target's Network domain once and keep polling.
                    if (unsupported && !pageTarget)
                    {
                        string? pageWs = await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        if (pageWs == null) return new(false, L.T("Browser.DebuggerTimeout", browser.Engine), null);

                        try { socket.Dispose(); } catch { }
                        socket = await ConnectAsync(pageWs, ct).ConfigureAwait(false);
                        pageTarget = true;
                        method = "Network.getAllCookies";
                        (await CallAsync(socket, ++id, "Network.enable", new { }, ct).ConfigureAwait(false))?.Dispose();
                        continue;
                    }
                }

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            return new(false, L.T("Browser.Login.Timeout"), null);
        }
        catch (OperationCanceledException)
        {
            return new(false, L.T("Browser.Login.Cancelled"), null);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("browser", "Browser sign-in failed", ex);
            return new(false, L.T("Browser.OpenFailed", ex.Message), null);
        }
        finally
        {
            try { socket?.Dispose(); } catch { }
            if (port > 0) await CloseBrowserAsync(port, proc).ConfigureAwait(false);
            try { proc?.Dispose(); } catch { }
            WipeProfileWithRetry(profileDir);
        }
    }

    /// <summary>
    /// Starts a hidden browser once, checks it answers over DevTools and closes it again — the same
    /// path sign-in uses, without opening a window. Reports which browser and version answered.
    /// </summary>
    public static async Task<OpenResult> TestAsync()
    {
        var browser = Resolve();
        if (browser == null) return new(false, L.T("Browser.NoneFound"), NoBrowser: true);

        string profileDir = NewProfileDir("test");
        Process? proc = null;
        int port = FreePort();
        try
        {
            var args = BaseArguments(profileDir, port);
            args.Add("--headless=new");
            args.Add("about:blank");
            try { proc = Start(browser, args); }
            catch (Exception ex) { return new(false, L.T("Browser.LaunchFailed", browser.Engine, ex.Message)); }

            string? wsUrl = await WaitForBrowserSocketAsync(port, TimeSpan.FromSeconds(20));
            if (wsUrl == null) return new(false, L.T("Browser.DebuggerTimeout", browser.Engine));

            using var socket = await ConnectAsync(wsUrl, CancellationToken.None);
            using var reply = await CallAsync(socket, 1, "Browser.getVersion", new { }, CancellationToken.None);
            string product = reply != null && reply.RootElement.TryGetProperty("result", out var r)
                             && r.TryGetProperty("product", out var pr) ? pr.GetString() ?? "" : "";
            return product.Length > 0
                ? new(true, L.T("Browser.Test.Ok", browser.Engine, product))
                : new(false, L.T("Browser.DebuggerTimeout", browser.Engine));
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("browser", "Browser test failed", ex);
            return new(false, L.T("Browser.OpenFailed", ex.Message));
        }
        finally
        {
            await CloseBrowserAsync(port, proc);
            try { proc?.Dispose(); } catch { }
            WipeProfileWithRetry(profileDir);
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(string wsUrl, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try { await socket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false); }
        catch { socket.Dispose(); throw; }
        return socket;
    }

    /// <summary>Whether the DevTools endpoint answers, and how many page (window/tab) targets it lists.</summary>
    private static async Task<(bool reachable, int pages)> ProbeAsync(HttpClient http, int port)
    {
        try
        {
            string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            int pages = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Count(t => t.TryGetProperty("type", out var ty) && ty.GetString() is "page" or "app")
                : 0;
            return (true, pages);
        }
        catch { return (false, 0); }
    }

    /// <summary>
    /// Ends a browser session the manager started: asks the browser itself to close over DevTools
    /// (that reaches the real browser process even when the launcher handed off and exited, and one
    /// left running in the background), then kills the launcher's process tree as a fallback.
    /// </summary>
    private static async Task CloseBrowserAsync(int port, Process? launcher)
    {
        try
        {
            string? ws = await WaitForBrowserSocketAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (ws != null)
            {
                using var socket = await ConnectAsync(ws, CancellationToken.None).ConfigureAwait(false);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                (await CallAsync(socket, 1, "Browser.close", new { }, cts.Token).ConfigureAwait(false))?.Dispose();
            }
        }
        catch { /* already gone, or closed before it could answer */ }

        try { if (launcher is { HasExited: false }) launcher.Kill(entireProcessTree: true); }
        catch (Exception ex) { DiagnosticsService.Warn("browser", "Could not stop the browser process", ex); }
    }

    /// <summary>Waits until a signed-in window is closed, then shuts that browser down and wipes its profile.</summary>
    private static async Task CleanupWhenClosedAsync(int port, Process? launcher, string profileDir)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var liveness = new BrowserLiveness();
            while (true)
            {
                await Task.Delay(2000).ConfigureAwait(false);
                var (reachable, pages) = await ProbeAsync(http, port).ConfigureAwait(false);
                if (liveness.Observe(reachable, pages)) break;
            }
            await CloseBrowserAsync(port, launcher).ConfigureAwait(false);
        }
        catch (Exception ex) { DiagnosticsService.Warn("browser", "Watching a browser window failed", ex); }
        finally
        {
            try { launcher?.Dispose(); } catch { }
            WipeProfileWithRetry(profileDir);
        }
    }

    private static async Task<(string? cookie, bool unsupported)> TryReadSessionCookieAsync(
        ClientWebSocket socket, int id, string method, CancellationToken ct)
    {
        using var doc = await CallAsync(socket, id, method, new { }, ct).ConfigureAwait(false);
        if (doc == null) return (null, false);

        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _)) return (null, true);
        if (!root.TryGetProperty("result", out var result)) return (null, false);
        if (!result.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
            return (null, false);

        foreach (var c in cookies.EnumerateArray())
        {
            string name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!string.Equals(name, ".ROBLOSECURITY", StringComparison.Ordinal)) continue;

            string domain = c.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
            if (!domain.EndsWith("roblox.com", StringComparison.OrdinalIgnoreCase)) continue;

            string value = c.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            // A logged-out placeholder has no warning prefix; only a real session does.
            if (value.Contains("WARNING", StringComparison.Ordinal)) return (value, false);
        }

        return (null, false);
    }

    // ---------------------------------------------------------------
    //  Process + profile plumbing
    // ---------------------------------------------------------------

    private static List<string> BaseArguments(string profileDir, int port) => new()
    {
        $"--user-data-dir={profileDir}",
        $"--remote-debugging-port={port}",
        "--remote-debugging-address=127.0.0.1",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-search-engine-choice-screen",
        "--disable-sync",
        "--disable-features=msEdgeSidebarV2,msEdgeShopping,msImplicitSignin",
    };

    private static Process? Start(BrowserInfo browser, List<string> args)
    {
        var psi = new ProcessStartInfo(browser.ExePath) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    private static string NewProfileDir(string label)
    {
        string dir = Paths.InData(Path.Combine("browser", $"{label}-{Guid.NewGuid().ToString("N")[..8]}"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Deletes leftover browser profiles under data/browser. Runs at start and exit so no site data
    /// from a previous session stays on disk. A profile whose browser is still open is locked; it is
    /// skipped and caught next time.
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
            // Up to ~10 s: a browser asked to close can take a few seconds to release its files.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    Directory.Delete(dir, true);
                    return;
                }
                catch { await Task.Delay(500); }
            }
            WipeSensitiveFiles(dir);
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

    private static async Task<string?> WaitForBrowserSocketAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws))
                {
                    string? url = ws.GetString();
                    if (!string.IsNullOrEmpty(url)) return url;
                }
            }
            catch { }
            await Task.Delay(250);
        }
        return null;
    }

    /// <summary>
    /// Sends one DevTools command and waits for the reply with the matching id, skipping protocol
    /// events in between. Null when nothing answered in time — the caller polls again.
    /// </summary>
    private static async Task<JsonDocument?> CallAsync(ClientWebSocket socket, int id, string method,
        object @params, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(new { id, method, @params });
        await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);

        var buffer = new byte[64 * 1024];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(ms.ToArray()); }
            catch { continue; }

            if (doc.RootElement.TryGetProperty("id", out var replyId)
                && replyId.TryGetInt32(out int got) && got == id)
                return doc;

            doc.Dispose();
        }

        return null;
    }

    private static async Task InjectCookieAndNavigateAsync(string wsUrl, string cookie, string? injectJs)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        var ct = CancellationToken.None;

        (await CallAsync(socket, 1, "Network.enable", new { }, ct))?.Dispose();
        // No "expires": a session cookie lives in browser memory only and dies with the window, so it
        // is never persisted into the profile's cookie database.
        (await CallAsync(socket, 2, "Network.setCookie", new
        {
            name = ".ROBLOSECURITY",
            value = cookie,
            domain = ".roblox.com",
            path = "/",
            secure = true,
            httpOnly = true,
        }, ct))?.Dispose();
        (await CallAsync(socket, 3, "Page.navigate", new { url = "https://www.roblox.com/home" }, ct))?.Dispose();

        if (!string.IsNullOrWhiteSpace(injectJs))
        {
            (await CallAsync(socket, 4, "Runtime.enable", new { }, ct))?.Dispose();
            await Task.Delay(2500);
            (await CallAsync(socket, 5, "Runtime.evaluate", new { expression = injectJs, userGesture = true, awaitPromise = false }, ct))?.Dispose();
        }

        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }
}
