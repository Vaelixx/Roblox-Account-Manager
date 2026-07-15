using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Views;

namespace RobloxAccountManager.Services;

/// <summary>Small helper so view-models can prompt without referencing Window types directly.</summary>
public static class DialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    /// <summary>
    /// Parents the dialog to the main window only if that window is actually
    /// visible on screen. Otherwise the dialog centers itself and gets its own
    /// taskbar entry — an owned dialog of an invisible window has no taskbar
    /// presence and can sit unnoticed while it blocks startup.
    /// </summary>
    private static void AttachOwner(Window dlg)
    {
        if (Owner != null && Owner != dlg && Owner.IsVisible)
        {
            dlg.Owner = Owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowInTaskbar = true;
        }
    }

    private static MessageDialog Make(MessageDialog.Kind kind, string title, string message,
        string initial = "", string okText = "OK", bool showCancel = true)
    {
        var dlg = new MessageDialog(kind, title, message, initial, okText, showCancel);
        AttachOwner(dlg);
        return dlg;
    }

    public static bool Confirm(string title, string message)
    {
        var dlg = Make(MessageDialog.Kind.Confirm, title, message, okText: "Confirm");
        return dlg.ShowDialog() == true;
    }

    public static void Info(string title, string message)
    {
        var dlg = Make(MessageDialog.Kind.Confirm, title, message, okText: "OK", showCancel: false);
        dlg.ShowDialog();
    }

    /// <summary>Shows a prompt with a Download button that opens the given URL when confirmed.</summary>
    public static void OfferDownload(string title, string message, string url)
    {
        var dlg = Make(MessageDialog.Kind.Confirm, title, message, okText: "Download");
        if (dlg.ShowDialog() == true)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }

    public static string? Prompt(string title, string label, string initial = "")
    {
        var dlg = Make(MessageDialog.Kind.Text, title, label, initial);
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }

    public static string? PromptPassword(string title, string message)
    {
        var dlg = Make(MessageDialog.Kind.Password, title, message);
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }

    /// <summary>Shows the Chromium download dialog. Returns true if Chromium is installed afterwards.</summary>
    public static bool ShowChromiumDownload()
    {
        var dlg = new ChromiumDownloadDialog();
        AttachOwner(dlg);
        dlg.ShowDialog();
        return dlg.Installed;
    }

    public static Account? ShowAddAccount(AccountStore store)
    {
        var dlg = new AddAccountDialog(store);
        AttachOwner(dlg);
        return dlg.ShowDialog() == true ? dlg.Added : null;
    }

    public static string? ShowImport()
    {
        var dlg = Make(MessageDialog.Kind.Multiline,
            "Import accounts",
            "Paste one or more cookies (any format — one per line or mixed text). Each is validated before it's added.",
            okText: "Import");
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }
}
