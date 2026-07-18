using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace RobloxAccountManager.Views;

/// <summary>
/// Post-update changelog: shown once on the first run of a new version, with the release
/// notes pulled from the matching GitHub release (best-effort; a miss shows a hint instead).
/// </summary>
public partial class WhatsNewWindow : Window
{
    private readonly string _releasePageUrl;

    public WhatsNewWindow(string versionText, string? notes, string? releasePageUrl)
    {
        InitializeComponent();

        VersionChip.Text = versionText;
        _releasePageUrl = string.IsNullOrEmpty(releasePageUrl)
            ? $"https://github.com/Vaelixx/Roblox-Account-Manager/releases/tag/{versionText}"
            : releasePageUrl;

        ReleaseNotesRenderer.Render(notes, NotesPanel);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Owner == null)
        {
            // Tray-only start: no visible owner to center on.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Rounded corners on Windows 11 (same treatment as UpdaterWindow).
        var hwnd = new WindowInteropHelper(this).Handle;
        int pref = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
    }

    private void ViewOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_releasePageUrl) { UseShellExecute = true }); }
        catch { /* browser launch is best-effort */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
