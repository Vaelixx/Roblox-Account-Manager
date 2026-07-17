using RobloxAccountManager.Models;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Services;

/// <summary>
/// Parses and executes command-line requests, both at startup and when forwarded from a
/// second instance via <see cref="SingleInstanceService"/>. Supported today:
/// <code>--launch "&lt;alias-or-username&gt;" &lt;placeId&gt; [jobId]</code>
/// Updater args (<c>--apply-update</c>, <c>--post-update</c>) are owned by <see cref="App"/>
/// and deliberately ignored here.
/// </summary>
public static class CliService
{
    private const string LaunchFlag = "--launch";

    /// <summary>True if <paramref name="args"/> carry an actionable request worth forwarding.</summary>
    public static bool HasActionableArgs(string[] args) =>
        args.Any(a => string.Equals(a, LaunchFlag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Executes the request against the running app. Returns a human-readable status
    /// (also mirrored into the status bar). The launch itself is awaited so callers can
    /// log the outcome, but it is safe to fire-and-forget.
    /// </summary>
    public static async Task<string> HandleAsync(MainViewModel vm, string[] args)
    {
        int idx = Array.FindIndex(args, a => string.Equals(a, LaunchFlag, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx + 2 >= args.Length)
            return "No actionable CLI arguments.";

        string who = args[idx + 1];

        var digits = new string(args[idx + 2].Where(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out long placeId) || placeId <= 0)
            return $"CLI: invalid placeId '{args[idx + 2]}'.";

        // Optional 4th token is a Job ID, unless it's the next flag.
        string? jobId = idx + 3 < args.Length && !args[idx + 3].StartsWith("--")
            ? args[idx + 3]
            : null;

        var acc = FindAccount(vm.Store, who);
        if (acc == null)
        {
            string miss = $"CLI: no account matching '{who}'.";
            vm.SetStatus(miss);
            return miss;
        }

        vm.SetStatus($"CLI launch: {acc.DisplayNameOrUser} → {placeId}…");
        var r = await LauncherService.LaunchAsync(acc, placeId, jobId);
        string msg = r.Success
            ? $"CLI launched {acc.DisplayNameOrUser}."
            : $"CLI launch failed: {r.Message}";
        vm.SetStatus(msg);
        return msg;
    }

    /// <summary>Prefer an exact alias, then exact username, then a case-insensitive contains match.</summary>
    private static Account? FindAccount(AccountStore store, string who)
    {
        return store.Accounts.FirstOrDefault(a => string.Equals(a.Alias, who, StringComparison.OrdinalIgnoreCase))
            ?? store.Accounts.FirstOrDefault(a => string.Equals(a.Username, who, StringComparison.OrdinalIgnoreCase))
            ?? store.Accounts.FirstOrDefault(a =>
                   (a.Username?.Contains(who, StringComparison.OrdinalIgnoreCase) ?? false)
                || (a.Alias?.Contains(who, StringComparison.OrdinalIgnoreCase) ?? false));
    }
}
