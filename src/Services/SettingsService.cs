using System.IO;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public static class SettingsService
{
    /// <summary>Schema of settings.json this build writes. Bump together with <see cref="Migrate"/>.</summary>
    public const int CurrentSchema = 2;

    private static string FilePath => Paths.InData("settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new() { SettingsVersion = CurrentSchema };

    private static readonly object _saveLock = new();

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                // A fresh install: nothing to migrate, but the defaults get the same checks as a file.
                Current = new AppSettings { SettingsVersion = CurrentSchema };
                Normalize(Current);
                return;
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (loaded == null) return;

            Current = loaded;
            if (Current.SettingsVersion < CurrentSchema)
            {
                // Keep the pre-migration file next to the new one: a user rolling back to v1.x can
                // restore it by hand, and a migration bug never costs anyone their settings.
                TryCopy(FilePath, FilePath + $".v{Current.SettingsVersion}.bak");
                Migrate(Current);
                Save();
            }
            if (Normalize(Current)) Save();
        }
        catch (Exception ex)
        {
            // A malformed file would otherwise be overwritten with defaults on the next Save().
            // Preserve it so the user (or a bug report) can see what went wrong.
            DiagnosticsService.Error("settings", "settings.json could not be read — starting with defaults", ex);
            TryCopy(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Current = new AppSettings { SettingsVersion = CurrentSchema };
        }
    }

    /// <summary>Upgrades a v1.x settings object in place.</summary>
    private static void Migrate(AppSettings s)
    {
        if (s.SettingsVersion < 2)
        {
            // Theme: the single preset list became mode + accent. The old presets only ever changed
            // the accent (Ocean also tinted the canvas), and their colours were copied into
            // CustomTheme — keys that mean something different now, so they are dropped.
            s.AccentName = s.ThemeName switch
            {
                "Indigo" => "Iris",
                "Emerald" => "Emerald",
                "Amber" => "Amber",
                "Rose" => "Rose",
                "Ocean" => "Ocean",
                _ => "Mono",
            };
            s.ThemeName = null;
            s.ThemeMode = ThemeService.ModeDark;
            s.CustomTheme = new Dictionary<string, string>();

            // FPS: the flag-based unlock stopped working with Roblox's FastFlag allowlist. Carry the
            // user's intent over to the in-game frame-rate cap, which the client still honours.
            if ((s.UnlockFps || s.FFlagUnlockFps) && s.MaxFps > 0)
                s.FpsCap = Math.Clamp(s.MaxFps, 30, 1000);
            s.UnlockFps = false;
            s.FFlagUnlockFps = false;
            s.MaxFps = 0;

            // An explicit language was always saved as "en" before, so that says nothing about a
            // choice. Only a non-English value was picked on purpose.
            if (s.Language == "en") s.Language = "";

            // Plugins used to load unconditionally. Keep them working for anyone who actually has
            // some installed; everybody else gets the safer default.
            try
            {
                string dir = PluginService.PluginDir;
                s.EnablePlugins = Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.dll").Any();
            }
            catch { s.EnablePlugins = false; }

            if (s.WindowWidth < 1180) s.WindowWidth = 1280;
            if (s.WindowHeight < 740) s.WindowHeight = 800;

            s.SettingsVersion = 2;
        }
    }

    /// <summary>
    /// Clamps values a hand-edited file could have pushed out of range. Returns true when a stored
    /// value had to be upgraded in a way worth saving straight away (a legacy preset destination —
    /// its private-server link moves into an encrypted field).
    /// </summary>
    private static bool Normalize(AppSettings s)
    {
        s.AccountJoinDelay = Math.Clamp(s.AccountJoinDelay, 0, 600);
        s.ShufflePageCount = Math.Clamp(s.ShufflePageCount, 1, 25);
        s.PresencePollSeconds = Math.Clamp(s.PresencePollSeconds, 5, 600);
        s.UpdateCheckMinutes = Math.Clamp(s.UpdateCheckMinutes, 15, 1440);
        s.WebApiPort = Math.Clamp(s.WebApiPort, 1024, 65535);
        s.AutoLockMinutes = Math.Clamp(s.AutoLockMinutes, 1, 240);
        s.ClipboardClearSeconds = Math.Clamp(s.ClipboardClearSeconds, 0, 600);
        s.FpsCap = s.FpsCap <= 0 ? 0 : Math.Clamp(s.FpsCap, 30, 1000);
        if (!ThemeService.Modes.Contains(s.ThemeMode)) s.ThemeMode = ThemeService.ModeDark;
        if (!ThemeService.AccentNames.Contains(s.AccentName)) s.AccentName = "Mono";
        s.CustomTheme ??= new();
        s.CustomFFlags ??= new();
        s.SavedPlaces ??= new();
        s.LaunchPresets ??= new();
        s.ScheduledTasks ??= new();
        s.Hotkeys ??= new();

        s.DisconnectMinutes = Math.Clamp(s.DisconnectMinutes, 2, 60);
        s.RestartClientsMinutes = Math.Clamp(s.RestartClientsMinutes, 30, 24 * 60);
        s.AntiAfkIntervalMinutes = Math.Clamp(s.AntiAfkIntervalMinutes, 1, 120);
        s.AntiAfkIntervalMaxMinutes = Math.Clamp(s.AntiAfkIntervalMaxMinutes, s.AntiAfkIntervalMinutes, 120);
        s.UltraLowAfk ??= new();
        s.UltraLowAfk.FpsCap = s.UltraLowAfk.FpsCap <= 0 ? 0 : Math.Clamp(s.UltraLowAfk.FpsCap, 5, 1000);

        bool upgraded = false;
        s.LastServerInput ??= "";
        s.LastFollowInput ??= "";
        s.SavedPlaces.RemoveAll(p => p == null);
        foreach (var place in s.SavedPlaces)
        {
            place.Server ??= "";
            if (string.IsNullOrEmpty(place.Id)) { place.Id = Guid.NewGuid().ToString("N"); upgraded = true; }
        }

        s.LaunchPresets.RemoveAll(p => p == null);
        foreach (var p in s.LaunchPresets)
        {
            p.Aliases ??= new();
            p.JobId ??= "";
            p.PrivateServerLink ??= "";
            p.FollowUsername ??= "";
            p.SavedPlaceId ??= "";
            p.JoinDelaySeconds = Math.Clamp(p.JoinDelaySeconds, 0, 600);
            p.RandomDelaySeconds = Math.Clamp(p.RandomDelaySeconds, 0, 600);
            if (!PerformanceProfiles.IsKnown(p.PerformanceProfile)) p.PerformanceProfile = PerformanceProfiles.Normal;
            upgraded |= p.NormalizeDestination();
        }
        return upgraded;
    }

    public static void Save()
    {
        // The settings are edited on the UI thread. A save from elsewhere (a launch on a timer thread)
        // would read the lists while the UI changes them, so it runs there too while the UI is up.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(new Action(Save));
            return;
        }

        try
        {
            lock (_saveLock)
            {
                // Taken inside the lock, so the snapshots reach the file in the order they were taken
                // and an older one can never overwrite a newer one.
                string json = JsonSerializer.Serialize(Current, JsonOpts);
                Directory.CreateDirectory(Paths.DataDir);
                // Atomic write: stage to a temp file then swap, so a crash mid-write can never
                // truncate settings.json.
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath))
                    File.Replace(tmp, FilePath, null);
                else
                    File.Move(tmp, FilePath);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("settings", "settings.json could not be saved", ex);
        }
    }

    private static void TryCopy(string from, string to)
    {
        try { if (File.Exists(from)) File.Copy(from, to, overwrite: true); }
        catch { /* best-effort */ }
    }
}

/// <summary>
/// Central place for on-disk locations. Data lives next to the executable (portable) when that
/// folder is writable or a "portable.txt" marker is present; otherwise it falls back to
/// %APPDATA%\RobloxAccountManager so installs under Program Files still work, and stays there
/// (see <see cref="DataFolderMigration"/>).
/// </summary>
public static class Paths
{
    public static string BaseDir => AppContext.BaseDirectory;

    private static string? _dataDir;
    public static string DataDir => _dataDir ??= ResolveDataDir();
    public static string InData(string file) => System.IO.Path.Combine(DataDir, file);

    public static bool IsPortable { get; private set; }

    /// <summary>Redirects all data to another folder. Only the debug demo mode uses this, before anything is read.</summary>
    internal static void OverrideDataDir(string dir)
    {
        _dataDir = dir;
        IsPortable = false;
    }

    private static string ResolveDataDir()
    {
        string local = System.IO.Path.Combine(BaseDir, "data");

        if (System.IO.File.Exists(System.IO.Path.Combine(BaseDir, "portable.txt")))
        {
            IsPortable = true;
            return local;
        }

        string perUser = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RobloxAccountManager", "data");
        // A data folder next to the exe that can't be written to: its contents come along instead of
        // starting empty, and the move is remembered (see DataFolderMigration — nothing is deleted).
        string chosen = DataFolderMigration.Choose(local, perUser);
        IsPortable = string.Equals(chosen, local, StringComparison.OrdinalIgnoreCase);
        return chosen;
    }
}
