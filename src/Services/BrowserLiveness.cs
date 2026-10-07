namespace RobloxAccountManager.Services;

/// <summary>
/// Decides when a browser window the manager opened has really been closed, from what its DevTools
/// endpoint reports rather than from the process <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// returned. That first process is not a reliable handle: a Chromium launcher can hand the window to
/// another process and exit straight away (so sign-in could report "closed" while the window was
/// still open), and a browser can stay alive in the background after its last window closed (which would
/// keep a sign-in waiting forever).
///
/// The window counts as open while the endpoint answers and lists at least one page. Only several
/// failed observations in a row count as closed, so one slow answer during a heavy page load is
/// not mistaken for the user closing the window. "No pages" only counts once a page has been seen:
/// a build that lists its window under an unexpected target type must not be declared closed.
/// </summary>
public sealed class BrowserLiveness
{
    public const int ClosedAfter = 3;

    private int _misses;
    private bool _sawPage;

    /// <summary>Records one probe. Returns true once the window has been seen gone <see cref="ClosedAfter"/> times in a row.</summary>
    public bool Observe(bool endpointReachable, int pageTargets)
    {
        if (endpointReachable && pageTargets > 0)
        {
            _sawPage = true;
            _misses = 0;
            return false;
        }
        if (endpointReachable && !_sawPage) return false;
        return ++_misses >= ClosedAfter;
    }
}
