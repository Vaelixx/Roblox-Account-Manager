namespace RobloxAccountManager.Services;

/// <summary>
/// Named bundles of the graphics options the manager already writes (FastFlags + the frame-rate
/// cap), applied for one launch. Only allowlisted flags are used — see <see cref="FFlagsService.Allowlist"/>.
///
/// Roblox reads both files once, when a client starts, and both are shared by every client on the
/// PC. A profile therefore affects the clients launched while it is applied, and the next launch
/// without a profile puts the normal settings back (see <see cref="UndoEdits"/>). There is no
/// per-client setting to change afterwards, and clients started at the same instant can race.
/// </summary>
public static class PerformanceProfiles
{
    public const string Normal = "";
    public const string UltraLowAfk = "UltraLowAfk";

    public static readonly string[] All = { Normal, UltraLowAfk };

    public static bool IsKnown(string? id) => Array.IndexOf(All, id ?? "") >= 0;

    /// <summary>Flags a profile sets on top of the user's normal FastFlags. Empty for the normal profile.</summary>
    public static Dictionary<string, string> Flags(string? id, UltraLowOptions options)
    {
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        if (id != UltraLowAfk) return f;

        if (options.LowestQuality) f["DFIntDebugFRMQualityLevelOverride"] = "1";
        if (options.NoAntiAliasing) f["FIntDebugForceMSAASamples"] = "0";
        if (options.LowestTextures)
        {
            f["DFFlagTextureQualityOverrideEnabled"] = "True";
            f["DFIntTextureQualityOverride"] = "0";
        }
        if (options.NoGrass)
        {
            f["FIntFRMMinGrassDistance"] = "0";
            f["FIntFRMMaxGrassDistance"] = "0";
        }
        if (options.GraySky) f["FFlagDebugSkyGray"] = "True";
        if (options.FreezeLighting) f["DFFlagDebugPauseVoxelizer"] = "True";   // lighting voxel updates
        return f;
    }

    /// <summary>Frame-rate cap the profile wants, or 0 to leave the normal cap in place.</summary>
    public static int FpsCap(string? id, UltraLowOptions options)
        => id == UltraLowAfk && options.FpsCap > 0 ? Math.Clamp(options.FpsCap, 5, 1000) : 0;

    /// <summary>Whether the profile minimizes its clients once they are in game.</summary>
    public static bool Minimizes(string? id, UltraLowOptions options) => id == UltraLowAfk && options.MinimizeWhenInGame;

    /// <summary>
    /// Undoes what an earlier profile launch left in the shared flag file. For every flag the profile
    /// wrote that this launch does not set itself, and whose value in the file is still exactly what
    /// the profile wrote, the value from before the profile comes back (or the flag is removed when
    /// there was none). A flag someone changed since — by hand or through a bootstrapper — is left alone.
    /// </summary>
    /// <returns>Flag → value to write, or null to delete it.</returns>
    public static Dictionary<string, string?> UndoEdits(
        IReadOnlyDictionary<string, string> writtenByProfile,
        IReadOnlyDictionary<string, string> previousValues,
        IReadOnlyDictionary<string, string> thisLaunch,
        IReadOnlyDictionary<string, string> inFile)
    {
        var edits = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, written) in writtenByProfile)
        {
            if (thisLaunch.ContainsKey(key)) continue;
            if (!inFile.TryGetValue(key, out var current) || !string.Equals(current, written, StringComparison.Ordinal)) continue;
            edits[key] = previousValues.TryGetValue(key, out var previous) ? previous : null;
        }
        return edits;
    }
}

/// <summary>What the Ultra-low AFK profile changes. Every part can be switched off on its own.</summary>
public sealed class UltraLowOptions
{
    public bool LowestQuality { get; set; } = true;
    public bool NoAntiAliasing { get; set; } = true;
    public bool LowestTextures { get; set; } = true;
    public bool NoGrass { get; set; } = true;
    public bool GraySky { get; set; } = true;
    public bool FreezeLighting { get; set; } = true;

    /// <summary>Frame-rate cap; 0 leaves the normal cap alone.</summary>
    public int FpsCap { get; set; } = 15;

    public bool MinimizeWhenInGame { get; set; } = true;
}
