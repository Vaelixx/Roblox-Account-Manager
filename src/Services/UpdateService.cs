using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services;

/// <summary>A newer release discovered on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, long Size)
{
    /// <summary>Short display form, e.g. "v1.1.0".</summary>
    public string VersionText => $"v{Version.ToString(3)}";
}

/// <summary>
/// Portable self-updater, stage 1: checks GitHub for a newer release and hands the actual
/// swap over to a copy of this exe running from %TEMP% (see App.OnStartup / UpdaterWindow).
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/Vaelixx/Roblox-Account-Manager/releases/latest";

    /// <summary>Folder the updater copy runs from; --post-update deletes it afterwards.</summary>
    public static string UpdateTempDir => Path.Combine(Path.GetTempPath(), "RobloxAccountManagerUpdate");

    // Tags aren't guaranteed to be "vX.Y.Z" (the first release is tagged "roblox_account_manager"),
    // so pull the first dotted number group out of the tag, falling back to the release name.
    private static readonly Regex VersionPattern = new(@"\d+(\.\d+){1,3}", RegexOptions.Compiled);

    /// <summary>Returns the available update, or null when up to date / on any error (silent).</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager"); // GitHub API requires a UA

            using var resp = await http.GetAsync(LatestReleaseUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;

            var remote = ParseVersion(
                root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null,
                root.TryGetProperty("name", out var name) ? name.GetString() : null);
            if (remote == null || remote <= CurrentVersion()) return null;

            // First .exe asset is the new single-file build; without one there is nothing to install.
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var asset in assets.EnumerateArray())
            {
                string assetName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                if (!assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;

                long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
                return new UpdateInfo(remote, url, size);
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Stage 1 hand-over: copies this exe to %TEMP%\RobloxAccountManagerUpdate\Updater.exe and starts
    /// it with the --apply-update contract. Returns true when the updater launched — the caller
    /// should then shut the application down so the exe file lock is released.
    /// </summary>
    public static bool BeginUpdate(UpdateInfo info)
    {
        try
        {
            // Single-file publish: Assembly.Location is empty; ProcessPath is the real exe path.
            string mainExe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running executable path.");

            Directory.CreateDirectory(UpdateTempDir);
            string updaterExe = Path.Combine(UpdateTempDir, "Updater.exe");
            File.Copy(mainExe, updaterExe, overwrite: true);

            // Contract: --apply-update "<mainExePath>" <mainPid> "<downloadUrl>" "<versionText>"
            var psi = new ProcessStartInfo(updaterExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(mainExe);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(info.DownloadUrl);
            psi.ArgumentList.Add(info.VersionText);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Failed to launch updater: {ex.Message}");
            return false;
        }
    }

    private static Version? ParseVersion(string? tagName, string? releaseName)
    {
        foreach (string? source in new[] { tagName, releaseName })
        {
            if (string.IsNullOrEmpty(source)) continue;
            var m = VersionPattern.Match(source);
            if (m.Success && Version.TryParse(m.Value, out var v)) return Normalize(v);
        }
        return null;
    }

    /// <summary>Missing components count as 0 so "1.1" compares as "1.1.0.0".</summary>
    private static Version Normalize(Version v)
        => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static Version CurrentVersion()
        => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));
}
