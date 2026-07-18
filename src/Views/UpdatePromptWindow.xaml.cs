using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

/// <summary>
/// Rich update prompt: current → new version, publish date, download size, full release
/// notes and a link to the GitHub release. DialogResult == true means "install now".
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private readonly string _releasePageUrl;

    public UpdatePromptWindow(UpdateInfo info)
    {
        InitializeComponent();

        CurrentVersionText.Text = UpdateService.CurrentVersionText;
        NewVersionText.Text = info.VersionText;

        var meta = new List<string>();
        if (info.PublishedAt is { } dt) meta.Add($"Published {dt.ToLocalTime():d MMM yyyy}");
        if (info.SizeText.Length > 0) meta.Add($"Download {info.SizeText}");
        meta.Add("github.com/Vaelixx/Roblox-Account-Manager");
        MetaText.Text = string.Join("   ·   ", meta);

        _releasePageUrl = string.IsNullOrEmpty(info.ReleasePageUrl)
            ? "https://github.com/Vaelixx/Roblox-Account-Manager/releases/latest"
            : info.ReleasePageUrl;

        ReleaseNotesRenderer.Render(info.Notes, NotesPanel);
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

    private void UpdateNow_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
