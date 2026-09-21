using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace RobloxAccountManager.Services;

/// <summary>
/// Clipboard access with two jobs beyond Clipboard.SetText.
///
/// Secrets (a session cookie, a 2FA code, the API token) are copied so Windows keeps them out of
/// clipboard history (Win+V) and cloud clipboard sync, and they are wiped again after
/// <c>ClipboardClearSeconds</c> — unless the user has copied something else in the meantime, which
/// is left alone.
///
/// Every call retries briefly: another application holding the clipboard open makes the first
/// attempt fail with a COMException, which used to surface as "could not access the clipboard".
/// </summary>
public static class ClipboardService
{
    private static DispatcherTimer? _timer;
    private static string? _pendingSecret;

    /// <summary>Copies ordinary text. Returns false when the clipboard stayed busy.</summary>
    public static bool CopyText(string text) => TrySet(() => Clipboard.SetText(text));

    /// <summary>Copies a secret: excluded from history and cloud sync, cleared after the configured delay.</summary>
    public static bool CopySecret(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        bool ok = TrySet(() =>
        {
            var data = new DataObject();
            data.SetText(text);
            // Documented clipboard formats honoured by Windows clipboard history and cloud clipboard.
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(data, copy: true);
        });

        if (!ok) return false;

        int seconds = SettingsService.Current.ClipboardClearSeconds;
        _pendingSecret = text;
        _timer?.Stop();
        if (seconds > 0)
            ScheduleClear(TimeSpan.FromSeconds(seconds));
        return true;
    }

    /// <summary>Removes a secret we put on the clipboard, if it is still the current content.</summary>
    public static void ClearSecretIfPresent()
    {
        string? secret = _pendingSecret;
        if (secret == null) return;
        try
        {
            if (!Clipboard.ContainsText() || Clipboard.GetText() != secret)
            {
                _pendingSecret = null;
                return;
            }

            Clipboard.Clear();
            _pendingSecret = null;
        }
        catch (COMException) { ScheduleClear(TimeSpan.FromMilliseconds(500)); }
        catch (ExternalException) { ScheduleClear(TimeSpan.FromMilliseconds(500)); }
    }

    private static void ScheduleClear(TimeSpan delay)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) => { _timer?.Stop(); ClearSecretIfPresent(); };
        _timer.Start();
    }

    private static bool TrySet(Action set)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { set(); return true; }
            catch (COMException) { Thread.Sleep(40 * (attempt + 1)); }
            catch (ExternalException) { Thread.Sleep(40 * (attempt + 1)); }
            catch (Exception ex)
            {
                DiagnosticsService.Warn("clipboard", "Copy failed", ex);
                return false;
            }
        }
        return false;
    }
}
