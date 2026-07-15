using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;

namespace RobloxAccountManager.Services;

/// <summary>
/// Manages a private, portable Chromium build kept under data/chromium. Used for "Open in browser"
/// so accounts open in a clean Chromium instance — never the user's Edge/Chrome and never their main
/// profile. Downloaded on demand from the official Chromium snapshot storage.
/// </summary>
public static class ChromiumService
{
    private static string Dir => Paths.InData("chromium");
    private static string ExtractRoot => Path.Combine(Dir, "chrome-win");
    public static string ChromePath => Path.Combine(ExtractRoot, "chrome.exe");

    public static bool IsInstalled => File.Exists(ChromePath);

    public record Progress(long Done, long Total, string Phase)
    {
        public double Fraction => Total > 0 ? (double)Done / Total : 0;
    }

    /// <summary>Downloads and extracts the latest Chromium snapshot. Safe to cancel.</summary>
    public static async Task DownloadAsync(IProgress<Progress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        progress.Report(new Progress(0, 0, "Finding latest Chromium…"));
        string rev = (await http.GetStringAsync(
            "https://storage.googleapis.com/chromium-browser-snapshots/Win_x64/LAST_CHANGE", ct)).Trim();

        string url = $"https://storage.googleapis.com/chromium-browser-snapshots/Win_x64/{rev}/chrome-win.zip";
        string zipPath = Path.Combine(Dir, "chrome-win.zip");

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
                progress.Report(new Progress(done, total, "Downloading Chromium…"));
            }
        }

        progress.Report(new Progress(0, 0, "Extracting…"));
        if (Directory.Exists(ExtractRoot)) Directory.Delete(ExtractRoot, true);
        ZipFile.ExtractToDirectory(zipPath, Dir);   // zip contains a chrome-win/ folder
        try { File.Delete(zipPath); } catch { }

        progress.Report(new Progress(1, 1, "Ready"));
    }
}
