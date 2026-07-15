using System.Reflection;

namespace RobloxAccountManager;

/// <summary>
/// Single source of truth for the app version. The value is baked in at build time
/// from &lt;Version&gt; in RobloxAccountManager.csproj (that drives AssemblyVersion),
/// so bumping the csproj is the ONLY place a release number ever needs to change —
/// GUI display and the self-updater both read from here.
/// </summary>
public static class AppInfo
{
    /// <summary>AssemblyVersion, e.g. 1.1.0.0. Falls back to 1.0.0.0 only if unreadable.</summary>
    public static Version Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>Three-part string, e.g. "1.1.0".</summary>
    public static string Number => Version.ToString(3);

    /// <summary>Compact badge form, e.g. "v1.1.0".</summary>
    public static string Short => $"v{Number}";

    /// <summary>Settings-page form, e.g. "Version 1.1.0".</summary>
    public static string Long => $"Version {Number}";
}
