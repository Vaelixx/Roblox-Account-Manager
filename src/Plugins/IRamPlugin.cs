using RobloxAccountManager.Models;

namespace RobloxAccountManager.Plugins;

/// <summary>
/// Contract an external plugin DLL implements. Drop the compiled DLL into the app's
/// <c>plugins</c> folder; on startup every public parameterless-constructible type that
/// implements this is instantiated and <see cref="OnLoad"/> is called with a host handle.
/// A throwing plugin is isolated — it never brings down the app or its siblings.
/// </summary>
public interface IRamPlugin
{
    /// <summary>Display name shown in the plugins list.</summary>
    string Name { get; }

    /// <summary>Plugin version string (free-form).</summary>
    string Version { get; }

    /// <summary>Called once after load. Use the host to read accounts, launch/close, log and subscribe to events.</summary>
    void OnLoad(IPluginHost host);

    /// <summary>Called on app shutdown so the plugin can release timers/sockets/files.</summary>
    void OnUnload();
}

/// <summary>The surface a plugin is given: read accounts, drive launches, log, and observe lifecycle events.</summary>
public interface IPluginHost
{
    /// <summary>App version (three-part, e.g. "1.1.0").</summary>
    string AppVersion { get; }

    /// <summary>Live snapshot of managed accounts (read-only view).</summary>
    IReadOnlyList<Account> GetAccounts();

    /// <summary>Launches an account by userId. Returns true on success.</summary>
    Task<bool> LaunchAsync(long userId, long placeId = 0, string? jobId = null);

    /// <summary>Kills every tracked client belonging to the account; returns how many were closed.</summary>
    int Close(long userId);

    /// <summary>Writes a line to the plugin log (prefixed with the plugin name by the host).</summary>
    void Log(string message);

    /// <summary>Raised after an account's client is launched.</summary>
    event Action<PluginEvent>? AccountLaunched;

    /// <summary>Raised for every tracked-client exit (user-close, kill or crash — the app can't always tell which).</summary>
    event Action<PluginEvent>? AccountClosed;

    /// <summary>Raised when the watchdog treats an exit as a crash it will recover: fires just before the auto-rejoin (only for AutoRejoin accounts).</summary>
    event Action<PluginEvent>? AccountCrashed;
}

/// <summary>Immutable payload for plugin lifecycle events.</summary>
public sealed record PluginEvent(long UserId, string Username, long PlaceId, string? JobId);
