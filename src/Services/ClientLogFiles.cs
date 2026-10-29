namespace RobloxAccountManager.Services;

/// <summary>Which Roblox log file belongs to which client (pure, so it can be tested; used by <see cref="ClientLogWatcher"/>).</summary>
public static class ClientLogFiles
{
    /// <summary>A game client's own log ("…_Player_XXXXX_last.log"), not a crash handler's or the installer's.</summary>
    public static bool IsClientLog(string name)
        => name.Contains("_Player_", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Installer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The log for a client of <paramref name="userId"/> started at <paramref name="startLocal"/>: the one
    /// created closest to that moment (3 s before to 20 s after), skipping logs another client holds and
    /// logs known to belong to a different account. Null when none fits.
    /// </summary>
    public static string? PickLog(IEnumerable<(string Path, DateTime CreatedLocal)> logs, DateTime startLocal, long userId,
        ISet<string> held, IReadOnlyDictionary<string, long> owners)
        => logs
            .Where(f => !held.Contains(f.Path) && !(owners.TryGetValue(f.Path, out long o) && o != userId))
            .Select(f => (f.Path, gap: (f.CreatedLocal - startLocal).TotalSeconds))
            .Where(x => x.gap > -3 && x.gap < 20)
            .OrderBy(x => Math.Abs(x.gap))
            .Select(x => x.Path)
            .FirstOrDefault();
}
