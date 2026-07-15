namespace RobloxAccountManager.Models;

public class AppSettings
{
    // ---- Launch ----
    public bool EnableMultiInstance { get; set; } = true;   // hold ROBLOX_singletonMutex open
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
    public bool ShowThumbnails { get; set; } = true;
    public bool ShowRobux { get; set; } = true;

    // ---- Interface ----
    public bool HideUsernames { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public string AccentColor { get; set; } = "#7B61FF";
    public int MaxRecentGames { get; set; } = 12;

    // ---- Data ----
    public long DefaultPlaceId { get; set; } = 0;
    public bool SkipChromiumPrompt { get; set; } = false;
    public List<SavedPlace> SavedPlaces { get; set; } = new();

    // ---- housekeeping ----
    public double WindowWidth { get; set; } = 1120;
    public double WindowHeight { get; set; } = 720;
}
