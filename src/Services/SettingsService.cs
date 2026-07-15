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

/// <summary>Central place for on-disk locations (next to the executable).</summary>
public static class Paths
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string DataDir => System.IO.Path.Combine(BaseDir, "data");
    public static string InData(string file) => System.IO.Path.Combine(DataDir, file);
}
