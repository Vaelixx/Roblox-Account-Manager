using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Rules for the launch bar's "Job ID / link" box: what is kept for the next start and what must not
/// be shown on screen. Network-free, so they are covered by the unit tests.
/// </summary>
public static class LaunchBarInput
{
    /// <summary>
    /// True for a private-server or share link. Anyone holding one can join that server, so it is kept
    /// out of the visible box while usernames are hidden for screen sharing.
    /// </summary>
    public static bool IsPrivateLink(string? text)
    {
        if (!JoinLinks.LooksLikeLink(text)) return false;
        var parsed = JoinLinks.Parse(text!);
        return parsed.LinkCode != null || parsed.ShareCode != null;
    }

    /// <summary>
    /// Whether the box is worth restoring at the next start. A specific public server (a bare Job ID, or a
    /// link that names one) has usually shut down hours later, and launching into it then fails instead of
    /// joining the game, so only longer-lived destinations are kept: private-server, share and game links.
    /// </summary>
    public static bool IsWorthRemembering(string? text)
    {
        string t = (text ?? "").Trim();
        if (!JoinLinks.LooksLikeLink(t)) return false;
        var parsed = JoinLinks.Parse(t);
        return parsed.LinkCode != null || parsed.ShareCode != null || parsed.JobId == null;
    }
}
