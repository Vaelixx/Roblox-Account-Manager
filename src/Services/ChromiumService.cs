using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace RobloxAccountManager.Services;

/// <summary>
/// Manages a private, portable CloakBrowser build (stealth Chromium — github.com/CloakHQ/CloakBrowser)
/// kept under data/cloakbrowser. Used for "Open in browser" so accounts open in a clean instance —
/// never the user's Edge/Chrome and never their main profile. Downloaded on demand from the newest
/// free GitHub release that ships a windows-x64 zip (the "-pro" tags only carry checksums; their
/// binaries are license-gated). A previously downloaded plain Chromium under data/chromium keeps
/// working as a fallback, since CloakBrowser is a drop-in Chromium binary with identical CLI flags.
/// </summary>
public static class ChromiumService
{
    private const string ReleasesUrl = "https://api.github.com/repos/CloakHQ/CloakBrowser/releases?per_page=30";
    private const string AssetName = "cloakbrowser-windows-x64.zip";

    private static string CloakDir => Paths.InData("cloakbrowser");
    private static string LegacyChromePath => Path.Combine(Paths.InData("chromium"), "chrome-win", "chrome.exe");

    private static string? _cachedExe;

    /// <summary>Browser exe to launch — CloakBrowser preferred, legacy Chromium fallback, "" if none.</summary>
    public static string ChromePath => FindExe() ?? "";

    public static bool IsInstalled => FindExe() != null;

    private static string? FindExe()
    {
        if (_cachedExe != null && File.Exists(_cachedExe)) return _cachedExe;
        _cachedExe = null;

        try
        {
            if (Directory.Exists(CloakDir))
                _cachedExe = Directory
                    .EnumerateFiles(CloakDir, "chrome.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
        }
        catch { }

        if (_cachedExe == null && File.Exists(LegacyChromePath))
            _cachedExe = LegacyChromePath;

        return _cachedExe;
    }

    public record Progress(long Done, long Total, string Phase)
    {
        public double Fraction => Total > 0 ? (double)Done / Total : 0;
    }

    /// <summary>Downloads and extracts the newest free CloakBrowser build. Safe to cancel.</summary>
    public static async Task DownloadAsync(IProgress<Progress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(CloakDir);

        // ~540 MB download — generous timeout so slow connections still make it.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager");

        // Newest release that actually ships the free windows-x64 binary.
        progress.Report(new Progress(0, 0, "Finding latest CloakBrowser…"));
        string json = await http.GetStringAsync(ReleasesUrl, ct);
        string? url = null, tag = null;
        using (var doc = JsonDocument.Parse(json))
        {
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (!rel.TryGetProperty("assets", out var assets)) continue;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var n) && n.GetString() == AssetName &&
                        asset.TryGetProperty("browser_download_url", out var u))
                    {
                        url = u.GetString();
                        tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                        break;
                    }
                }
                if (url != null) break;
            }
        }
        if (url == null)
            throw new InvalidOperationException("No free CloakBrowser windows-x64 build found on GitHub.");

        string zipPath = Path.Combine(CloakDir, AssetName);
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress.Report(new Progress(done, total, $"Downloading CloakBrowser {tag}…"));
            }
        }

        progress.Report(new Progress(0, 0, "Extracting…"));
        // Wipe any previous build, keep the fresh zip until extraction succeeded.
        foreach (var d in Directory.GetDirectories(CloakDir))
        {
            try { Directory.Delete(d, true); } catch { }
        }
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, CloakDir), ct);
        try { File.Delete(zipPath); } catch { }

        _cachedExe = null;
        if (FindExe() == null || _cachedExe == LegacyChromePath)
            throw new InvalidOperationException("Archive extracted but no chrome.exe found inside.");

        progress.Report(new Progress(1, 1, "Ready"));
    }
}
