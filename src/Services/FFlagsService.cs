using System.IO;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Writes FastFlags to <c>%LOCALAPPDATA%\Roblox\Versions\&lt;hash&gt;\ClientSettings\ClientAppSettings.json</c>.
/// Convenience toggles in <see cref="AppSettings"/> expand into well-known flags; the user's
/// raw <see cref="AppSettings.CustomFFlags"/> are merged last so they always win.
/// </summary>
public static class FFlagsService
{
    /// <summary>
    /// Applies the configured flags to every installed Roblox version folder.
    /// No-op when <see cref="AppSettings.ApplyFFlags"/> is off. Returns folders written.
    /// </summary>
    public static int Apply(AppSettings s)
    {
        if (!s.ApplyFFlags) return 0;

        var flags = BuildFlags(s);
        int written = 0;
        foreach (var versionDir in VersionFolders())
        {
            try
            {
                var csDir = Path.Combine(versionDir, "ClientSettings");
                Directory.CreateDirectory(csDir);
                var json = JsonSerializer.Serialize(flags, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(csDir, "ClientAppSettings.json"), json);
                written++;
            }
            catch { /* a locked/permission-denied version folder shouldn't block the rest */ }
        }
        return written;
    }

    /// <summary>Removes ClientAppSettings.json from every version folder (revert to stock).</summary>
    public static int Clear()
    {
        int cleared = 0;
        foreach (var versionDir in VersionFolders())
        {
            try
            {
                var f = Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");
                if (File.Exists(f)) { File.Delete(f); cleared++; }
            }
            catch { }
        }
        return cleared;
    }

    /// <summary>Expands toggles + custom flags into the final string→string map Roblox expects.</summary>
    public static Dictionary<string, string> BuildFlags(AppSettings s)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (s.FFlagUnlockFps)
        {
            // The scheduler cap is what actually gates FPS; 0 = uncapped, then set a high target.
            flags["DFIntTaskSchedulerTargetFps"] = s.MaxFps > 0 ? s.MaxFps.ToString() : "9999";
            flags["FFlagDebugGraphicsDisableDirect3D11"] = "False";
        }
        if (s.FFlagDisableTelemetry)
        {
            flags["FFlagDebugDisableTelemetryEphemeralCounter"] = "True";
            flags["FFlagDebugDisableTelemetryEventIngest"] = "True";
            flags["FFlagDebugDisableTelemetryPoint"] = "True";
            flags["FFlagDebugDisableTelemetryV2Counter"] = "True";
        }
        if (s.FFlagLightingTechVoxel)
        {
            flags["FFlagDebugForceFutureIsBrightPhase3"] = "False";
            flags["DFFlagDebugRenderForceTechnologyVoxel"] = "True";
        }

        // Raw user flags win over the convenience toggles.
        foreach (var kv in s.CustomFFlags)
            if (!string.IsNullOrWhiteSpace(kv.Key))
                flags[kv.Key.Trim()] = kv.Value ?? "";

        return flags;
    }

    /// <summary>All <c>Versions\*</c> dirs that contain a RobloxPlayerBeta.exe (client installs).</summary>
    private static IEnumerable<string> VersionFolders()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox", "Versions");
        if (!Directory.Exists(root)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(root))
            if (File.Exists(Path.Combine(dir, "RobloxPlayerBeta.exe")))
                yield return dir;
    }
}
