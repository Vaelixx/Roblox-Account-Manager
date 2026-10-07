using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Turns what the user typed — a Place ID, a Job ID, a private-server or share link, a username —
/// into a <see cref="JoinTarget"/>. The launch bar and presets both go through here, so a link means
/// the same thing wherever it is pasted.
/// </summary>
public static class JoinTargetResolver
{
    /// <summary>A resolved target, or a user-facing reason why there is none.</summary>
    public sealed record Result(JoinTarget? Target, string? Error = null, string? ErrorTitle = null)
    {
        public static Result Fail(string error, string? title = null) => new(null, error, title);
    }

    /// <summary>
    /// The launch bar's "Job ID / link" box together with its place. A link can carry its own place,
    /// so <paramref name="placeId"/> may be 0 when <paramref name="input"/> is one.
    /// </summary>
    /// <param name="cookies">
    /// Sessions to resolve a modern share link with (the resolve API needs a signed-in account), best first.
    /// A session Roblox rejects says nothing about the link, so the next one is tried.
    /// </param>
    public static async Task<Result> FromServerInputAsync(string? input, long placeId, Func<IEnumerable<string>> cookies)
    {
        string text = (input ?? "").Trim();

        if (text.Length > 0 && JoinLinks.LooksLikeLink(text))
        {
            var parsed = JoinLinks.Parse(text);
            long pid = parsed.PlaceId > 0 ? parsed.PlaceId : placeId;

            if (parsed.LinkCode != null && pid > 0)
                return new(new JoinTarget(pid, LinkCode: parsed.LinkCode));

            if (parsed.ShareCode != null)
                return await ResolveShareLinkAsync(parsed.ShareCode, cookies());

            return pid > 0 ? new(new JoinTarget(pid, JobId: parsed.JobId)) : Result.Fail(L.T("Launch.LinkNoPlace"));
        }

        if (placeId <= 0) return Result.Fail(L.T("Launch.NeedPlace"), L.T("Launch.NeedPlaceTitle"));
        string? jobId = JoinLinks.NormalizeJobId(text);
        if (text.Length > 0 && jobId == null)
            return Result.Fail(L.T("Launch.BadJobId"), L.T("Launch.BadJobIdTitle"));
        return new(new JoinTarget(placeId, JobId: jobId));
    }

    /// <summary>
    /// A share link, read with the first session Roblox accepts. A link Roblox does not know and Roblox
    /// being unreachable are reported as what they are, so a network hiccup never reads as a broken link.
    /// </summary>
    private static async Task<Result> ResolveShareLinkAsync(string shareCode, IEnumerable<string> cookies)
    {
        foreach (string cookie in cookies.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList())
        {
            var res = await RobloxApi.ResolveShareLinkAsync(cookie, shareCode);
            if (res.Info != null) return new(new JoinTarget(res.Info.PlaceId, LinkCode: res.Info.LinkCode));
            if (res.Status == RobloxApi.ShareLinkStatus.SessionRejected) continue;
            return res.Status == RobloxApi.ShareLinkStatus.Unreachable
                ? Result.Fail(L.T("Launch.LinkUnreachable", res.Error), L.T("Launch.FailedTitle"))
                : Result.Fail(L.T("Launch.LinkInvalid"), L.T("Launch.FailedTitle"));
        }
        return Result.Fail(L.T("Launch.LinkSessionsRejected"), L.T("Launch.FailedTitle"));
    }

    /// <summary>A user id typed as a number, or looked up from a username (a leading @ is ignored). 0 when not found.</summary>
    public static async Task<long> ResolveUserIdAsync(string? text)
    {
        string user = (text ?? "").Trim().TrimStart('@');
        if (user.Length == 0) return 0;
        if (long.TryParse(user, out long id)) return id > 0 ? id : 0;
        try { return await RobloxApi.GetUserIdAsync(user); }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("launcher", "Username lookup failed", ex);
            return 0;
        }
    }

    /// <summary>The destination a preset launches into.</summary>
    /// <param name="cookies">Sessions to resolve a share link with, best first (see <see cref="FromServerInputAsync"/>).</param>
    public static async Task<Result> ForPresetAsync(LaunchPreset p, Func<IEnumerable<string>> cookies)
    {
        if (p.SavedPlaceId.Length > 0)
        {
            var saved = SettingsService.Current.SavedPlaces.FirstOrDefault(s => s.Id == p.SavedPlaceId);
            if (saved == null) return Result.Fail(L.T("Automation.Saved.Missing"));
            // Same rules as the launch bar the bookmark was saved from.
            return await FromServerInputAsync(saved.Server, saved.PlaceId, cookies);
        }

        switch (p.Destination)
        {
            case JoinKind.FollowUser:
            {
                long id = p.FollowUserId > 0 ? p.FollowUserId : await ResolveUserIdAsync(p.FollowUsername);
                return id > 0
                    ? new(new JoinTarget(0, FollowUserId: id))
                    : Result.Fail(p.FollowUsername.Length > 0 ? L.T("Follow.NotFound", p.FollowUsername) : L.T("Follow.EnterUser"));
            }

            case JoinKind.PrivateServer:
            {
                if (!JoinLinks.LooksLikeLink(p.PrivateServerLink)) return Result.Fail(L.T("Automation.Preset.NeedLink"));
                var r = await FromServerInputAsync(p.PrivateServerLink, p.PlaceId, cookies);
                // A plain game link resolves to a public server; a preset marked private must not silently do that.
                return r.Target is { Kind: not JoinKind.PrivateServer } ? Result.Fail(L.T("Automation.Preset.NeedLink")) : r;
            }

            case JoinKind.Server:
                if (p.PlaceId <= 0) return Result.Fail(L.T("Launch.NeedPlace"));
                return await FromServerInputAsync(p.JobId, p.PlaceId, cookies);

            default:
                return p.PlaceId > 0 ? new(new JoinTarget(p.PlaceId)) : Result.Fail(L.T("Launch.NeedPlace"));
        }
    }
}
