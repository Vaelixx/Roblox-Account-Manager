using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

namespace RobloxAccountManager.Services;

/// <summary>
/// Edits the Roblox client's own settings file (<c>%LOCALAPPDATA%\Roblox\GlobalBasicSettings_N.xml</c>)
/// — the file the in-game Settings menu writes.
///
/// This is where the frame-rate cap lives now. The old approach, the DFIntTaskSchedulerTargetFps
/// FastFlag, stopped working when Roblox limited ClientAppSettings.json to an allowlist (September
/// 2025); the <c>FramerateCap</c> setting is the supported route and the one the in-game menu uses.
///
/// The number in the file name is a schema version Roblox bumps occasionally, so the highest one
/// present is used rather than a hard-coded "_13". Nothing is written when the file does not exist
/// yet: Roblox creates it on first run with its full set of defaults, and a hand-made stub could
/// replace those.
/// </summary>
public static class RobloxClientSettingsService
{
    private static readonly Regex FileVersion = new(@"^GlobalBasicSettings_(\d+)\.xml$", RegexOptions.IgnoreCase);

    public static string RobloxDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");

    /// <summary>The newest GlobalBasicSettings file, or null when Roblox has not created one yet.</summary>
    public static string? SettingsFile()
    {
        try
        {
            if (!Directory.Exists(RobloxDataDir)) return null;
            return Directory.EnumerateFiles(RobloxDataDir, "GlobalBasicSettings_*.xml")
                .Select(f => (path: f, m: FileVersion.Match(Path.GetFileName(f))))
                .Where(x => x.m.Success)
                .OrderByDescending(x => int.TryParse(x.m.Groups[1].Value, out int v) ? v : 0)
                .Select(x => x.path)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Current frame-rate cap from the file, or null when unknown.</summary>
    public static int? ReadFramerateCap()
    {
        try
        {
            string? file = SettingsFile();
            if (file == null) return null;
            var doc = XDocument.Load(file);
            var el = doc.XPathSelectElement("//Item[@class='UserGameSettings']/Properties/int[@name='FramerateCap']");
            return el != null && int.TryParse(el.Value, out int v) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Writes the frame-rate cap. Returns false when there is no settings file yet or it could not be
    /// written (Roblox holds it while a client is shutting down; the next launch tries again).
    /// </summary>
    public static bool WriteFramerateCap(int cap)
    {
        if (cap == 0) return false;   // negative is Roblox's own "default"; restoring it after a profile is legitimate
        string? file = SettingsFile();
        if (file == null) return false;

        try
        {
            var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
            var props = doc.XPathSelectElement("//Item[@class='UserGameSettings']/Properties");
            if (props == null) return false;

            var el = props.Elements("int").FirstOrDefault(e => (string?)e.Attribute("name") == "FramerateCap");
            string value = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (el == null)
            {
                props.Add(new XElement("int", new XAttribute("name", "FramerateCap"), value));
            }
            else
            {
                if (el.Value == value) return true;
                el.Value = value;
            }

            // A read-only file (some FPS guides tell people to lock it) would make the save throw;
            // respect the lock rather than silently removing it.
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)) return false;

            string tmp = file + ".ram.tmp";
            doc.Save(tmp, SaveOptions.DisableFormatting);
            File.Replace(tmp, file, null);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("client-settings", "Could not write the frame-rate cap", ex);
            try { File.Delete(file + ".ram.tmp"); } catch { }
            return false;
        }
    }
}
