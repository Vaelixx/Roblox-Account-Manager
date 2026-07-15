using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace RobloxAccountManager.Views;

/// <summary>
/// Portable self-updater, stage 2. This process is a copy of the app running from
/// %TEMP%\RobloxAccountManagerUpdate\Updater.exe (started with --apply-update): it waits for the
/// main app to exit, downloads the new exe, swaps it in place, then relaunches the app with
/// --post-update so the temp folder gets cleaned up.
/// </summary>
public partial class UpdaterWindow : Window
{
    private const int MaxReplaceAttempts = 20;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MainExitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _mainExePath;
    private readonly int _mainPid;
    private readonly string _downloadUrl;
    private readonly string _versionText;
    private readonly string _tempDir;

    private CancellationTokenSource? _downloadCts;
    private bool _running;

    public UpdaterWindow(string mainExePath, string mainPid, string downloadUrl, string versionText)
    {
        InitializeComponent();

        _mainExePath = mainExePath;
        _mainPid = int.TryParse(mainPid, out int pid) ? pid : 0;
        _downloadUrl = downloadUrl;
        _versionText = versionText;

        // We live in the update temp dir; download next to ourselves so --post-update removes both.
        string? procDir = Path.GetDirectoryName(Environment.ProcessPath);
        _tempDir = string.IsNullOrEmpty(procDir)
            ? Path.Combine(Path.GetTempPath(), "RobloxAccountManagerUpdate")
            : procDir;

        Loaded += async (_, _) => await RunAsync();
    }

    // ---- Windows 11 rounded corners (same DWM treatment as MainWindow) ----
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    // ---- update pipeline ----
    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;

        RetryButton.Visibility = Visibility.Collapsed;
        StartAnywayButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;

        try
        {
            // 1) Wait for the main app to exit so its exe file lock is released.
            SetStatus("Waiting for Roblox Account Manager to close…");
            await WaitForMainExitAsync();

            // 2) Download the new exe next to this updater.
            SetStatus($"Downloading {_versionText}…");
            string downloadPath = Path.Combine(_tempDir, "update.exe");
            _downloadCts = new CancellationTokenSource();
            await DownloadAsync(downloadPath, _downloadCts.Token);

            // 3) Swap the exe in place — point of no return, so cancel is disabled here.
            CancelButton.IsEnabled = false;
            SetStatus($"Installing {_versionText}…");
            await ReplaceMainExeAsync(downloadPath);

            // 4) Relaunch the updated app; it cleans this temp folder up in the background.
            SetStatus("Starting the updated app…");
            var psi = new ProcessStartInfo(_mainExePath) { UseShellExecute = false };
            psi.ArgumentList.Add("--post-update");
            psi.ArgumentList.Add(_tempDir);
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            // User cancelled the download: put the old app back on screen.
            StartOldAppAndExit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Updater] Update failed: {ex}");
            SetStatus($"Update failed: {Shorten(ex.Message)}");
            Progress.Value = 0;
            PercentText.Text = "";
            CancelButton.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;
            StartAnywayButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            _running = false;
        }
    }

    private async Task WaitForMainExitAsync()
    {
        if (_mainPid <= 0) return;
        try
        {
            using var proc = Process.GetProcessById(_mainPid);
            using var timeout = new CancellationTokenSource(MainExitTimeout);
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { /* still alive after 30s — try the swap anyway */ }
        }
        catch (ArgumentException) { }        // already exited
        catch (InvalidOperationException) { } // exited between lookup and wait
    }

    private async Task DownloadAsync(string destination, CancellationToken ct)
    {
        // No overall HttpClient timeout: large file on a slow line; cancel comes from the token.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager");

        using var resp = await http.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                int pct = (int)(done * 100 / total);
                Progress.Value = pct;
                PercentText.Text = $"{pct}%";
            }
            else
            {
                PercentText.Text = $"{done / (1024.0 * 1024.0):0.0} MB";
            }
        }

        if (done == 0) throw new IOException("The downloaded file is empty.");
    }

    private async Task ReplaceMainExeAsync(string downloadPath)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= MaxReplaceAttempts; attempt++)
        {
            try
            {
                File.Copy(downloadPath, _mainExePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex; // old exe still locked (straggling process, antivirus scan) — wait and retry
                await Task.Delay(ReplaceRetryDelay);
            }
        }
        throw new IOException($"Could not replace the application executable after {MaxReplaceAttempts} attempts.", last);
    }

    // ---- buttons ----
    private void Retry_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private void StartAnyway_Click(object sender, RoutedEventArgs e) => StartOldAppAndExit();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // During download: cancel the token and let RunAsync unwind into StartOldAppAndExit.
        if (_downloadCts != null) { _downloadCts.Cancel(); return; }
        StartOldAppAndExit(); // still waiting for the app to close — just bail out
    }

    private void StartOldAppAndExit()
    {
        try { Process.Start(new ProcessStartInfo(_mainExePath) { UseShellExecute = false }); }
        catch { }
        Application.Current.Shutdown();
    }

    // ---- helpers ----
    private void SetStatus(string text) => StatusText.Text = text;

    private static string Shorten(string s) => s.Length <= 120 ? s : s[..117] + "…";

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
