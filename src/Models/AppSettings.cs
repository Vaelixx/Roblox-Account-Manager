namespace RobloxAccountManager.Models;

public class AppSettings
{
    // ---- Launch ----
    public bool EnableMultiInstance { get; set; } = true;   // hold ROBLOX_singletonMutex open

    // Newer Roblox clients keep a second guard, ROBLOX_singletonEvent, inside their own process.
    // Holding the mutex does nothing about it, which is why a launch started from the website or
    // the Roblox home screen used to be swallowed by the client that was already running.
    // Closing that handle in every live client makes those launches open a new window too.
    public bool CloseSingletonEvent { get; set; } = true;
    public int SingletonWatchSeconds { get; set; } = 2;      // how often new clients are checked
    public bool AdoptExternalClients { get; set; } = true;   // manage clients started outside the app
    public bool MultiInstanceStartupCheck { get; set; } = true; // warn once if the guard can't run

    public int AccountJoinDelay { get; set; } = 8;          // seconds between sequential launches
    public bool AutoCloseLastProcess { get; set; } = true;  // close the same account's previous client
    public bool ShuffleLowestServer { get; set; } = false;  // "join" picks the emptiest server
    public int ShufflePageCount { get; set; } = 5;          // server pages scanned when shuffling
    public bool RememberWindowPositions { get; set; } = false;

    // ---- FPS ----
    public bool UnlockFps { get; set; } = false;
    public int MaxFps { get; set; } = 240;

    // ---- Presence / live data ----
    public bool ShowPresence { get; set; } = true;
    public int PresenceUpdateRate { get; set; } = 5;        // minutes
    public int PresencePollSeconds { get; set; } = 10;      // live dashboard poll cadence (seconds)
    public bool ShowThumbnails { get; set; } = true;
    public bool ShowRobux { get; set; } = true;
    public bool TrackEconomy { get; set; } = true;         // collectible RAP + premium membership

    // ---- Interface ----
    public bool HideUsernames { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public string AccentColor { get; set; } = "#7B61FF";
    public int MaxRecentGames { get; set; } = 12;
    public string LastSeenVersion { get; set; } = "";       // last version the "What's new" window ran for
    public string UpdateNotesSeenFor { get; set; } = "";    // one-shot: version whose notes the update prompt already showed

    // ---- Appearance (#30 UX-Politur: Theme-Editor / Views / i18n / Notif) ----
    public string ThemeName { get; set; } = "Avallon (mono)";          // built-in preset name
    public Dictionary<string, string> CustomTheme { get; set; } = new(); // per-key hex overrides
    public string AccountViewMode { get; set; } = "Card";              // Card | Compact
    public string Language { get; set; } = "en";                       // en | de
    public bool EnableToasts { get; set; } = true;                     // in-app toast notifications
    public bool ToastOnLaunch { get; set; } = true;                    // toast when a client launches
    public bool ToastOnCrash { get; set; } = true;                     // toast when a client crashes

    // ---- Data ----
    public long DefaultPlaceId { get; set; } = 0;
    public bool SkipChromiumPrompt { get; set; } = false;
    public List<SavedPlace> SavedPlaces { get; set; } = new();

    // ---- Anti-AFK ----
    public bool AntiAfkEnabled { get; set; } = false;
    public int AntiAfkIntervalMinutes { get; set; } = 15;   // one input per this many minutes
    public string AntiAfkKey { get; set; } = "Space";       // Space/Shift/Ctrl/W/A/S/D/0/F13
    public bool AntiAfkRestoreFocus { get; set; } = true;   // return to the user's window afterwards

    // ---- Crash watchdog ----
    public bool WatchdogEnabled { get; set; } = false;
    public int WatchdogCheckSeconds { get; set; } = 30;

    // ---- Notifications ----
    public string DiscordWebhookUrl { get; set; } = "";
    public bool NotifyOnCrash { get; set; } = true;    // disconnect + reconnect embeds
    public bool NotifyOnConnect { get; set; } = false; // embed when an account launches/connects

    // ---- Startup checks ----
    public bool ValidateCookiesOnStartup { get; set; } = false;

    // ---- Presets / scheduler ----
    public List<LaunchPreset> LaunchPresets { get; set; } = new();
    public List<ScheduledTask> ScheduledTasks { get; set; } = new();

    // ---- FastFlags ----
    // Written to <RobloxVersion>\ClientSettings\ClientAppSettings.json before launch.
    public bool ApplyFFlags { get; set; } = false;
    // Convenience toggles that expand into well-known flags at write time.
    public bool FFlagUnlockFps { get; set; } = false;       // DFIntTaskSchedulerTargetFps
    public bool FFlagDisableTelemetry { get; set; } = false;
    public bool FFlagLightingTechVoxel { get; set; } = false;
    // Raw user-supplied flags, merged last so they always win.
    public Dictionary<string, string> CustomFFlags { get; set; } = new();

    // ---- Proxy (for this manager's Roblox web/API calls) ----
    public bool EnableProxy { get; set; } = false;
    public string ProxyAddress { get; set; } = "";          // http://host:port or socks5://host:port
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";

    // ---- Web API (localhost control server) ----
    public bool WebApiEnabled { get; set; } = false;
    public int WebApiPort { get; set; } = 7963;             // 127.0.0.1:{port}, user-scoped bind
    public string WebApiToken { get; set; } = "";           // bearer token; empty = surface disabled

    // ---- RAM monitor ----
    public bool RamMonitorEnabled { get; set; } = false;
    public int RamMonitorSeconds { get; set; } = 10;        // poll interval
    public bool AutoCloseOnHighRam { get; set; } = false;   // kill a client over the limit
    public int RamLimitMb { get; set; } = 4096;             // per-client working-set cap

    // ---- global hotkeys (#29 Power-Tools) ----
    // Disabled by default so a fresh install never steals a system-wide chord, but
    // each slot is pre-filled with a sensible combo the user only has to toggle on.
    // Modifiers bitmask: Alt=1, Ctrl=2, Shift=4, Win=8. Key = Win32 virtual-key code.
    public List<HotkeyBinding> Hotkeys { get; set; } = new()
    {
        new HotkeyBinding { Action = "LaunchSelected",    Modifiers = 3, Key = 0x4C }, // Ctrl+Alt+L
        new HotkeyBinding { Action = "ServerHopSelected", Modifiers = 3, Key = 0x48 }, // Ctrl+Alt+H
        new HotkeyBinding { Action = "CloseAllRoblox",    Modifiers = 3, Key = 0x4B }, // Ctrl+Alt+K
        new HotkeyBinding { Action = "FocusManager",      Modifiers = 3, Key = 0x52 }, // Ctrl+Alt+R
    };

    // ---- security-extra ----
    public bool AutoLockEnabled { get; set; } = false;          // lock the app after idle
    public int AutoLockMinutes { get; set; } = 10;              // idle minutes before locking
    public bool AuditLogEnabled { get; set; } = false;          // append security events to data/audit.log
    public bool RotationDetectionEnabled { get; set; } = true;  // capture rotated .ROBLOSECURITY on launch

    // ---- housekeeping ----
    public double WindowWidth { get; set; } = 1120;
    public double WindowHeight { get; set; } = 720;
}
