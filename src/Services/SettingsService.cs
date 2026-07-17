using System.IO;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public static class SettingsService
{
    private static readonly string Path_ = Paths.InData("settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

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
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(Current, JsonOpts));
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
