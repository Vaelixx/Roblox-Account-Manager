using RobloxAccountManager.Models;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Services;

/// <summary>
/// Parses and executes command-line requests, both at startup and when forwarded from a
/// second instance via <see cref="SingleInstanceService"/>. Supported today:
/// <code>--launch "&lt;alias-or-username&gt;" &lt;placeId or game link&gt; [jobId or private-server link]</code>
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

        if (LockService.IsLocked)
        {
            vm.SetStatus(L.T("Lock.Blocked"));
            return L.T("Lock.Blocked");
        }

        string who = args[idx + 1];
        string place = args[idx + 2];

        // Same reading as the launch bar's Place box: a game link gives the id in its path. Keeping every
        // digit turned ".../games/8737899170/Pet-Simulator-99" into place 873789917099.
        long placeId = JoinLinks.ParsePlaceId(place);
        if (placeId <= 0 && !JoinLinks.LooksLikeLink(place))
            return L.T("Cli.BadPlace", place);

        // Optional 4th token is a Job ID or a link, unless it's the next flag. Without one, a link given as
        // the place still counts, so its private-server code or server id isn't dropped.
        string? server = idx + 3 < args.Length && !args[idx + 3].StartsWith("--")
            ? args[idx + 3]
            : JoinLinks.LooksLikeLink(place) ? place : null;

        var acc = FindAccount(vm.Store, who);
        if (acc == null)
        {
            string miss = L.T("Cli.NoAccount", who);
            vm.SetStatus(miss);
            return miss;
        }

        // The launch bar's rules: links are read the same way, and a malformed Job ID is refused instead
        // of being sent to Roblox.
        var resolved = await JoinTargetResolver.FromServerInputAsync(server, placeId, () => new[] { acc.Cookie });
        if (resolved.Target == null)
        {
            string error = resolved.Error ?? L.T("Cli.BadPlace", place);
            vm.SetStatus(error);
            return error;
        }

        vm.SetStatus(L.T("Cli.Launching", acc.DisplayNameOrUser));
        var r = await LauncherService.LaunchAsync(acc, resolved.Target);
        string msg = r.Success
            ? L.T("Status.Launched", acc.DisplayNameOrUser)
            : r.Message;
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
