using System.IO;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Writes FastFlags into ClientAppSettings.json before a launch.
///
/// Since September 2025 the Roblox client only honours flags on an allowlist and silently ignores
/// everything else. The convenience options therefore only ever produce allowlisted flags, and the
/// custom-flag editor points out the ones Roblox will ignore instead of letting them look applied.
/// The allowlist is Roblox's to change — it is kept here as data, and unknown flags are still
/// written (a future allowlist may accept them), just flagged in the UI.
/// </summary>
public static class FFlagsService
{
    /// <summary>Roblox's published allowlist for local client configuration.</summary>
    public static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
        // Geometry
        "DFIntCSGLevelOfDetailSwitchingDistance",
        "DFIntCSGLevelOfDetailSwitchingDistanceL12",
        "DFIntCSGLevelOfDetailSwitchingDistanceL23",
        "DFIntCSGLevelOfDetailSwitchingDistanceL34",
        // Rendering
        "FFlagHandleAltEnterFullscreenManually",
        "DFFlagTextureQualityOverrideEnabled",
        "DFIntTextureQualityOverride",
        "FIntDebugForceMSAASamples",
        "DFFlagDisableDPIScale",
        "FFlagDebugGraphicsPreferD3D11",
        "FFlagDebugSkyGray",
        "DFFlagDebugPauseVoxelizer",
        "DFIntDebugFRMQualityLevelOverride",
        "FIntFRMMaxGrassDistance",
        "FIntFRMMinGrassDistance",
        "FFlagDebugGraphicsPreferVulkan",
        "FFlagDebugGraphicsPreferOpenGL",
        // User interface
        "FIntGrassMovementReducedMotionFactor",
    };

    /// <summary>
    /// Everything a launch writes: the convenience options, the user's raw flags, the launch's
    /// performance profile, then this account's own overrides. Also applies the frame-rate cap to
    /// Roblox's settings file. Returns how many flag files were written.
    /// </summary>
    /// <remarks>
    /// Both files are shared by every client and read when a client starts, so a profile is undone by
    /// the next launch that does not use it (<see cref="PerformanceProfiles.UndoEdits"/>). What that
    /// needs is kept in <see cref="AppSettings.ProfileUndo"/>; the caller saves the settings.
    /// </remarks>
    public static int ApplyForLaunch(AppSettings s, Account? account = null, string? profile = null)
    {
        ApplyFramerate(s, profile);

        var flags = s.ApplyFFlags ? BuildFlags(s) : new Dictionary<string, string>(StringComparer.Ordinal);
        var profileFlags = PerformanceProfiles.Flags(profile, s.UltraLowAfk);
        foreach (var kv in profileFlags)
            flags[kv.Key] = kv.Value;
        foreach (var kv in ParseRaw(account?.FFlags))
            flags[kv.Key] = kv.Value;

        var undo = s.ProfileUndo;
        if (flags.Count == 0 && (undo == null || undo.Written.Count == 0)) return 0;

        // What this launch's profile leaves in the file (an account override can win over a profile flag).
        var written = profileFlags.Keys.ToDictionary(k => k, k => flags[k], StringComparer.Ordinal);
        var previous = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();

        int count = Write(flags, failed, (dir, file) =>
        {
            var oldPrevious = undo != null && undo.Previous.TryGetValue(dir, out var p) ? p : new Dictionary<string, string>();

            // Remember what the profile overwrites, unless an earlier profile launch already did —
            // then the file holds that profile's value and the original is the one already recorded.
            var keep = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in written.Keys)
            {
                bool stillProfiles = undo != null && undo.Written.TryGetValue(key, out var w)
                                     && file.TryGetValue(key, out var cur) && cur == w;
                if (stillProfiles) { if (oldPrevious.TryGetValue(key, out var orig)) keep[key] = orig; }
                else if (file.TryGetValue(key, out var existing)) keep[key] = existing;
            }
            if (keep.Count > 0) previous[dir] = keep;

            if (undo != null)
                foreach (var (key, value) in PerformanceProfiles.UndoEdits(undo.Written, oldPrevious, flags, file))
                {
                    if (value == null) file.Remove(key);
                    else file[key] = value;
                }
        });

        // A plain launch that could not reach every flag file keeps the record: the profile's flags may still
        // be in the file it missed, and undoing only touches flags that still hold the profile's values.
        if (written.Count == 0 && undo != null && failed.Count > 0) return count;
        s.ProfileUndo = written.Count > 0 ? new ProfileUndoState { Written = written, Previous = previous } : null;
        return count;
    }

    /// <summary>The profile's frame-rate cap, the user's own cap, or the cap from before a profile.</summary>
    private static void ApplyFramerate(AppSettings s, string? profile)
    {
        try
        {
            int profileFps = PerformanceProfiles.FpsCap(profile, s.UltraLowAfk);
            if (profileFps > 0)
            {
                if (s.FpsCapBeforeProfile == null)
                {
                    // What to put back later; no cap in the file is Roblox's default (-1). If the file can't
                    // be read right now, the profile's cap isn't written either, or the user's would be lost.
                    if (!RobloxClientSettingsService.TryReadFramerateCap(out int? current)) return;
                    s.FpsCapBeforeProfile = current ?? -1;
                }
                if (RobloxClientSettingsService.WriteFramerateCap(profileFps)) s.FpsCapWrittenByProfile = profileFps;
                return;
            }

            bool restored;
            if (s.FpsCap > 0)
                restored = RobloxClientSettingsService.WriteFramerateCap(s.FpsCap);
            else if (s.FpsCapBeforeProfile is int before)
            {
                if (!RobloxClientSettingsService.TryReadFramerateCap(out int? current)) return;   // the next launch tries again
                // The profile's cap is still there: put the user's back. Anything else they changed in game since.
                restored = s.FpsCapWrittenByProfile is not int ours || current != ours
                           || RobloxClientSettingsService.WriteFramerateCap(before);
            }
            else return;

            // A running Ultra-low client writes its cap back into the file when it closes, so the record
            // stays until none is left and the next plain launch restores the cap once more.
            bool profileStillRunning = ProcessRegistry.All.Any(t => t.Profile == PerformanceProfiles.UltraLowAfk);
            if (restored && !profileStillRunning)
            {
                s.FpsCapBeforeProfile = null;
                s.FpsCapWrittenByProfile = null;
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("fflags", "Could not apply the frame-rate cap", ex); }
    }

    /// <summary>
    /// Parses a raw ClientAppSettings JSON blob into a flat string→string map. Non-string values are
    /// stringified so <c>{"X": true}</c> and <c>{"X": "True"}</c> behave the same. Invalid JSON yields
    /// an empty map rather than throwing — a half-typed blob must never block a launch.
    /// </summary>
    public static Dictionary<string, string> ParseRaw(string? json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(prop.Name)) continue;
                map[prop.Name.Trim()] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Null => "",
                    _ => prop.Value.GetRawText()
                };
            }
        }
        catch { }
        return map;
    }

    public static bool IsValidRaw(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>Flags in <paramref name="flags"/> the current client will ignore.</summary>
    public static List<string> NotAllowlisted(IEnumerable<string> flags)
        => flags.Where(f => !Allowlist.Contains(f)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Writes the flags into every ClientSettings folder the installed client(s) read — for a
    /// bootstrapper (Bloxstrap, Fishstrap…) that is its own managed file. Existing flags are merged,
    /// not replaced: a bootstrapper's file is the user's configuration.
    /// </summary>
    /// <param name="failed">Collects the folders that could not be written, when given.</param>
    /// <param name="adjust">Runs on each file's current flags before <paramref name="flags"/> are merged in.</param>
    private static int Write(Dictionary<string, string> flags, List<string>? failed = null,
        Action<string, Dictionary<string, string>>? adjust = null)
    {
        int written = 0;
        foreach (var csDir in RobloxInstallService.FlagTargetDirectories())
        {
            try
            {
                Directory.CreateDirectory(csDir);
                string file = Path.Combine(csDir, "ClientAppSettings.json");

                var merged = new Dictionary<string, string>(StringComparer.Ordinal);
                if (File.Exists(file))
                    foreach (var kv in ParseRaw(File.ReadAllText(file)))
                        merged[kv.Key] = kv.Value;
                adjust?.Invoke(csDir, merged);
                foreach (var kv in flags) merged[kv.Key] = kv.Value;

                string tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, file, overwrite: true);
                written++;
            }
            catch (Exception ex)
            {
                DiagnosticsService.Warn("fflags", $"Could not write flags into {csDir}", ex);
                failed?.Add(csDir);
            }
        }
        return written;
    }

    /// <summary>Removes ClientAppSettings.json from every flag target (revert to stock).</summary>
    public static int Clear()
    {
        SettingsService.Current.ProfileUndo = null;   // nothing left to undo
        int cleared = 0;
        foreach (var csDir in RobloxInstallService.FlagTargetDirectories())
        {
            try
            {
                var f = Path.Combine(csDir, "ClientAppSettings.json");
                if (File.Exists(f)) { File.Delete(f); cleared++; }
            }
            catch { }
        }
        return cleared;
    }

    /// <summary>Expands the convenience options plus the custom flags into the map Roblox expects.</summary>
    public static Dictionary<string, string> BuildFlags(AppSettings s)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        switch (s.GraphicsApi)
        {
            case "D3D11": flags["FFlagDebugGraphicsPreferD3D11"] = "True"; break;
            case "Vulkan": flags["FFlagDebugGraphicsPreferVulkan"] = "True"; break;
            case "OpenGL": flags["FFlagDebugGraphicsPreferOpenGL"] = "True"; break;
        }

        if (s.MsaaSamples is 0 or 1 or 2 or 4 or 8)
            flags["FIntDebugForceMSAASamples"] = s.MsaaSamples.ToString();

        if (s.TextureQuality is >= 0 and <= 3)
        {
            flags["DFFlagTextureQualityOverrideEnabled"] = "True";
            flags["DFIntTextureQualityOverride"] = s.TextureQuality.ToString();
        }

        if (s.QualityLevelOverride is >= 1 and <= 21)
            flags["DFIntDebugFRMQualityLevelOverride"] = s.QualityLevelOverride.ToString();

        if (s.DisableDpiScale) flags["DFFlagDisableDPIScale"] = "True";
        if (s.GraySky) flags["FFlagDebugSkyGray"] = "True";
        if (s.PauseVoxelizer) flags["DFFlagDebugPauseVoxelizer"] = "True";
        if (s.AltEnterFullscreen) flags["FFlagHandleAltEnterFullscreenManually"] = "False";
        if (s.HideGrass)
        {
            flags["FIntFRMMinGrassDistance"] = "0";
            flags["FIntFRMMaxGrassDistance"] = "0";
        }

        // Raw user flags win over the convenience options.
        foreach (var kv in s.CustomFFlags)
            if (!string.IsNullOrWhiteSpace(kv.Key))
                flags[kv.Key.Trim()] = kv.Value ?? "";

        return flags;
    }
}
