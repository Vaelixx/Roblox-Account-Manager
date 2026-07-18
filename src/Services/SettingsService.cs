using System.IO;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public static class SettingsService
{
    private static readonly string Path_ = Paths.InData("settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    private static readonly object _saveLock = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_));
                if (loaded != null) Current = loaded;
            }
        }
        catch
        {
            // A malformed/incompatible file would otherwise be silently overwritten with
            // defaults on the next Save(), wiping every setting. Preserve it for recovery
            // instead so the user (or a bug report) can see what went wrong.
            TryPreserveCorruptFile();
            Current = new AppSettings();
        }
    }

    private static void TryPreserveCorruptFile()
    {
        try
        {
            if (!File.Exists(Path_)) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(Path_, Path_ + ".corrupt-" + stamp, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    public static void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Current, JsonOpts);
            lock (_saveLock)
            {
                Directory.CreateDirectory(Paths.DataDir);
                // Atomic write: stage to a temp file then swap, so a crash mid-write can
                // never truncate settings.json.
                string tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(Path_))
                    File.Replace(tmp, Path_, null);
                else
                    File.Move(tmp, Path_);
            }
        }
        catch { /* best-effort */ }
    }
}

/// <summary>
/// Central place for on-disk locations. Data lives next to the executable (portable)
/// when that folder is writable or a "portable.txt" marker is present; otherwise it
/// falls back to %APPDATA%\RobloxAccountManager so installs under Program Files still work.
/// </summary>
public static class Paths
{
    public static string BaseDir => AppContext.BaseDirectory;

    private static string? _dataDir;
    public static string DataDir => _dataDir ??= ResolveDataDir();
    public static string InData(string file) => System.IO.Path.Combine(DataDir, file);

    public static bool IsPortable { get; private set; }

    private static string ResolveDataDir()
    {
        string local = System.IO.Path.Combine(BaseDir, "data");

        // Explicit portable marker, or an already-initialised local data folder -> stay local.
        if (System.IO.File.Exists(System.IO.Path.Combine(BaseDir, "portable.txt"))
            || System.IO.Directory.Exists(local))
        {
            IsPortable = true;
            return local;
        }

        // Otherwise keep it local only if we can actually write there.
        try
        {
            System.IO.Directory.CreateDirectory(local);
            string probe = System.IO.Path.Combine(local, ".wtest");
            System.IO.File.WriteAllText(probe, "");
            System.IO.File.Delete(probe);
            IsPortable = true;
            return local;
        }
        catch
        {
            IsPortable = false;
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RobloxAccountManager", "data");
        }
    }
}
