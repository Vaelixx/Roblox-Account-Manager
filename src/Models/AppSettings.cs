using System.Text.Json.Serialization;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Models;

public class AppSettings
{
    /// <summary>
    /// Schema version of settings.json. 0 means "written by v1.x" — see SettingsService.Migrate.
    /// A brand-new install starts at <see cref="SettingsService.CurrentSchema"/>.
    /// </summary>
    public int SettingsVersion { get; set; } = 0;

    // ---- Launch ----
    public bool EnableMultiInstance { get; set; } = true;   // hold ROBLOX_singletonMutex open

    // Newer Roblox clients keep a second guard, ROBLOX_singletonEvent, inside their own process.
    // Closing that handle in every live client makes launches from the website open a new window.
    public bool CloseSingletonEvent { get; set; } = true;
    public int SingletonWatchSeconds { get; set; } = 2;
    public bool AdoptExternalClients { get; set; } = true;
    public bool MultiInstanceStartupCheck { get; set; } = true;

    public int AccountJoinDelay { get; set; } = 8;          // seconds between sequential launches
    public bool AutoCloseLastProcess { get; set; } = true;  // close the same account's previous client
    public bool ShuffleLowestServer { get; set; } = false;  // smart join picks the emptiest server
    public int ShufflePageCount { get; set; } = 5;          // server pages scanned

    /// <summary>
    /// Frame-rate cap written into Roblox's own settings file before a launch; 0 leaves Roblox's
    /// setting alone. Replaces the old DFIntTaskSchedulerTargetFps flag, which the client has
    /// ignored since Roblox's FastFlag allowlist (September 2025).
    /// </summary>
    public int FpsCap { get; set; } = 0;

    // ---- Presence / live data ----
    public bool ShowPresence { get; set; } = true;
    public int PresencePollSeconds { get; set; } = 10;
    public bool ShowThumbnails { get; set; } = true;
    public bool ShowRobux { get; set; } = true;
    public bool TrackEconomy { get; set; } = true;          // collectible RAP + premium membership
    public bool TrackPlaytime { get; set; } = true;

    // ---- Interface ----
    public bool HideUsernames { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public string LastSeenVersion { get; set; } = "";
    public string UpdateNotesSeenFor { get; set; } = "";
    public bool SidebarCollapsed { get; set; } = false;
    public string AccountSort { get; set; } = "Name";       // Name | Recent | Robux | Playtime | Status
    public bool GroupAccounts { get; set; } = true;

    // ---- Appearance ----
    public string ThemeMode { get; set; } = ThemeService.ModeDark;   // Dark | Light | System
    public string AccentName { get; set; } = "Mono";
    public Dictionary<string, string> CustomTheme { get; set; } = new();
    public string AccountViewMode { get; set; } = "Card";            // Card | Compact
    public string Language { get; set; } = "";                       // "" = follow Windows

    /// <summary>v1.x theme preset name; read once for migration and never written again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThemeName { get; set; }

    // ---- Notifications ----
    public bool EnableToasts { get; set; } = true;
    public bool ToastOnLaunch { get; set; } = true;
    public bool ToastOnCrash { get; set; } = true;

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string DiscordWebhookUrl { get; set; } = "";
    public bool NotifyOnCrash { get; set; } = true;
    public bool NotifyOnConnect { get; set; } = false;

    // ---- Data ----
    public long DefaultPlaceId { get; set; } = 0;
    public bool SkipChromiumPrompt { get; set; } = false;
    public List<SavedPlace> SavedPlaces { get; set; } = new();

    /// <summary>The launch bar's server box (Job ID or private/share link) and player box from the last launch.</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string LastServerInput { get; set; } = "";
    public string LastFollowInput { get; set; } = "";

    // ---- Anti-AFK ----
    public bool AntiAfkEnabled { get; set; } = false;
    public int AntiAfkIntervalMinutes { get; set; } = 15;
    public string AntiAfkKey { get; set; } = "Space";
    public bool AntiAfkRestoreFocus { get; set; } = true;

    /// <summary>
    /// Pick each client's next interval at random between <see cref="AntiAfkIntervalMinutes"/> and
    /// <see cref="AntiAfkIntervalMaxMinutes"/>, so several clients don't all get their key press at once.
    /// </summary>
    public bool AntiAfkRandomize { get; set; } = false;
    public int AntiAfkIntervalMaxMinutes { get; set; } = 14;

    // ---- Crash watchdog ----
    public bool WatchdogEnabled { get; set; } = false;
    public int WatchdogCheckSeconds { get; set; } = 30;

    /// <summary>
    /// Also rejoin when an account with a running client stops showing as in game (a disconnect
    /// dialog keeps the process alive, so the exit-based watchdog never sees it). Needs presence.
    /// </summary>
    public bool RejoinOnDisconnect { get; set; } = false;
    public int DisconnectMinutes { get; set; } = 3;

    /// <summary>Close and relaunch every client after it has run this long (fresh session, memory back).</summary>
    public bool RestartClientsEnabled { get; set; } = false;
    public int RestartClientsMinutes { get; set; } = 180;

    // ---- Startup checks ----
    public bool ValidateCookiesOnStartup { get; set; } = false;

    // ---- Presets / scheduler ----
    public List<LaunchPreset> LaunchPresets { get; set; } = new();
    public List<ScheduledTask> ScheduledTasks { get; set; } = new();

    // ---- FastFlags ----
    // Written to ClientAppSettings.json before launch. Since September 2025 the client only honours
    // flags on Roblox's allowlist, so the convenience options below are all allowlisted ones.
    public bool ApplyFFlags { get; set; } = false;
    public string GraphicsApi { get; set; } = "Auto";       // Auto | D3D11 | Vulkan | OpenGL
    public int MsaaSamples { get; set; } = -1;              // -1 auto, 0/1/2/4/8
    public int TextureQuality { get; set; } = -1;           // -1 auto, 0..3
    public int QualityLevelOverride { get; set; } = 0;      // 0 auto, 1..21
    public bool DisableDpiScale { get; set; } = false;
    public bool HideGrass { get; set; } = false;
    public bool GraySky { get; set; } = false;
    public bool PauseVoxelizer { get; set; } = false;
    public bool AltEnterFullscreen { get; set; } = false;
    public Dictionary<string, string> CustomFFlags { get; set; } = new();

    // ---- Performance profiles (see PerformanceProfiles) ----
    public UltraLowOptions UltraLowAfk { get; set; } = new();

    /// <summary>What the last profile launch changed in the flag files, so the next normal launch can put it back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProfileUndoState? ProfileUndo { get; set; }

    /// <summary>Roblox's frame-rate cap from before a profile changed it; null when no profile is in effect.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FpsCapBeforeProfile { get; set; }

    /// <summary>The cap the profile wrote, so it is only put back while the file still holds it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FpsCapWrittenByProfile { get; set; }

    // ---- Proxy (the manager's own Roblox web calls) ----
    public bool EnableProxy { get; set; } = false;
    public string ProxyAddress { get; set; } = "";
    public string ProxyUsername { get; set; } = "";

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string ProxyPassword { get; set; } = "";

    // ---- Browser ----
    /// <summary>Auto | CloakBrowser | Edge | Chrome — which browser opens accounts and browser sign-ins.</summary>
    public string BrowserEngine { get; set; } = "Auto";

    // ---- Web API (localhost control server) ----
    public bool WebApiEnabled { get; set; } = false;
    public int WebApiPort { get; set; } = 7963;

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string WebApiToken { get; set; } = "";

    /// <summary>The /cookie endpoint hands out full account access, so it is off unless asked for.</summary>
    public bool WebApiAllowCookieRead { get; set; } = false;

    // ---- Plugins ----
    /// <summary>Plugins run arbitrary code inside the manager, so loading them is opt-in.</summary>
    public bool EnablePlugins { get; set; } = false;

    // ---- RAM monitor ----
    public bool RamMonitorEnabled { get; set; } = false;
    public int RamMonitorSeconds { get; set; } = 10;
    public bool AutoCloseOnHighRam { get; set; } = false;
    public int RamLimitMb { get; set; } = 4096;
    public bool AutoTrimEnabled { get; set; } = false;
    public int AutoTrimMinutes { get; set; } = 10;

    // ---- Global hotkeys ----
    // Disabled by default so a fresh install never steals a system-wide chord.
    // Modifiers bitmask: Alt=1, Ctrl=2, Shift=4, Win=8. Key = Win32 virtual-key code.
    public List<HotkeyBinding> Hotkeys { get; set; } = new()
    {
        new HotkeyBinding { Action = "LaunchSelected",    Modifiers = 3, Key = 0x4C }, // Ctrl+Alt+L
        new HotkeyBinding { Action = "ServerHopSelected", Modifiers = 3, Key = 0x48 }, // Ctrl+Alt+H
        new HotkeyBinding { Action = "CloseAllRoblox",    Modifiers = 3, Key = 0x4B }, // Ctrl+Alt+K
        new HotkeyBinding { Action = "FocusManager",      Modifiers = 3, Key = 0x52 }, // Ctrl+Alt+R
    };

    // ---- Security ----
    public bool AutoLockEnabled { get; set; } = false;          // lock after idle (needs a master password)
    public int AutoLockMinutes { get; set; } = 10;
    public bool LockOnMinimize { get; set; } = false;           // lock when hidden to the tray
    public int ClipboardClearSeconds { get; set; } = 30;        // wipe copied cookies / codes (0 = never)
    public bool AuditLogEnabled { get; set; } = false;
    public bool RotationDetectionEnabled { get; set; } = true;

    // ---- Updates ----
    public bool AutoCheckUpdates { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public int UpdateCheckMinutes { get; set; } = 60;
    public bool IncludePrereleases { get; set; } = false;
    public string SkippedUpdateVersion { get; set; } = "";
    public bool VerifyUpdateDownload { get; set; } = true;
    public bool KeepUpdateBackup { get; set; } = true;

    // ---- Windows startup ----
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;

    // ---- housekeeping ----
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; } = false;

    // ---- v1.x fields, read for migration only ----
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool UnlockFps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int MaxFps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool FFlagUnlockFps { get; set; }
}

/// <summary>Flags a performance profile wrote, and per flag file the values they replaced.</summary>
public class ProfileUndoState
{
    public Dictionary<string, string> Written { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> Previous { get; set; } = new();
}
