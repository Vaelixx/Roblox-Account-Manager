using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Views;

namespace RobloxAccountManager.Services;

/// <summary>Lets view-models prompt the user without referencing window types directly.</summary>
public static class DialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    private static int _openModals;

    /// <summary>True while any modal dialog is open — the main window dims itself behind it.</summary>
    public static bool IsModalOpen => _openModals > 0;

    /// <summary>Raised on the UI thread whenever <see cref="IsModalOpen"/> may have changed.</summary>
    public static event Action? ModalStateChanged;

    /// <summary>
    /// Parents the dialog to the main window only when that window is on screen. An owned dialog of
    /// an invisible window has no taskbar presence and can sit unnoticed while it blocks startup.
    /// </summary>
    private static void AttachOwner(Window dlg, Window? preferred = null)
    {
        var owner = preferred ?? Owner;
        if (owner != null && owner != dlg && owner.IsVisible)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowInTaskbar = true;
        }
    }

    /// <summary>Shows a modal and keeps the dimming scrim in sync with it.</summary>
    public static bool? ShowModal(Window dlg, Window? owner = null)
    {
        AttachOwner(dlg, owner);
        _openModals++;
        try { ModalStateChanged?.Invoke(); } catch { }
        try { return dlg.ShowDialog(); }
        finally
        {
            _openModals = Math.Max(0, _openModals - 1);
            try { ModalStateChanged?.Invoke(); } catch { }
        }
    }

    public static bool Confirm(string title, string message, string? okText = null, string? cancelText = null, bool danger = false)
    {
        var dlg = new MessageDialog(MessageDialog.Kind.Confirm, title, message, "",
            okText ?? L.T("Common.Confirm"), true, cancelText ?? L.T("Common.Cancel"), danger);
        return ShowModal(dlg) == true;
    }

    public static void Info(string title, string message)
    {
        var dlg = new MessageDialog(MessageDialog.Kind.Confirm, title, message, "", L.T("Common.Ok"), false, "");
        ShowModal(dlg);
    }

    /// <summary>A prompt with a Download button that opens <paramref name="url"/> when confirmed.</summary>
    public static void OfferDownload(string title, string message, string url)
    {
        var dlg = new MessageDialog(MessageDialog.Kind.Confirm, title, message, "", L.T("Common.Download"), true, L.T("Common.NotNow"));
        if (ShowModal(dlg) == true) BrowserService.OpenUrl(url);
    }

    public static string? Prompt(string title, string label, string initial = "", string? okText = null)
    {
        var dlg = new MessageDialog(MessageDialog.Kind.Text, title, label, initial, okText ?? L.T("Common.Ok"), true, L.T("Common.Cancel"));
        return ShowModal(dlg) == true ? dlg.ResultText : null;
    }

    public static string? PromptMultiline(string title, string label, string initial = "", string? okText = null)
    {
        var dlg = new MessageDialog(MessageDialog.Kind.Multiline, title, label, initial, okText ?? L.T("Common.Ok"), true, L.T("Common.Cancel"));
        return ShowModal(dlg) == true ? dlg.ResultText : null;
    }

    /// <summary>
    /// Asks for a password. With <paramref name="validate"/> the prompt stays open until the check
    /// passes: it returns null for a good password or the message to show under the box.
    /// </summary>
    public static string? PromptPassword(string title, string message, string? okText = null,
        Func<string, Task<string?>>? validate = null)
    {
        var dlg = new MessageDialog(MessageDialog.Kind.Password, title, message, "", okText ?? L.T("Common.Ok"), true, L.T("Common.Cancel"))
        {
            Validate = validate,
        };
        return ShowModal(dlg) == true ? dlg.ResultText : null;
    }

    /// <summary>Asks for a new password twice. Null when cancelled or the two entries differ (the user is told).</summary>
    public static string? PromptNewPassword(string title, string message, int minLength)
    {
        var dlg = new MessageDialog(MessageDialog.Kind.NewPassword, title, message, "", L.T("Common.Save"), true, L.T("Common.Cancel"))
        {
            MinLength = minLength,
        };
        return ShowModal(dlg) == true ? dlg.ResultText : null;
    }

    /// <summary>Shows the CloakBrowser download. Returns true when it is installed afterwards.</summary>
    public static bool ShowChromiumDownload(Window? owner = null)
    {
        var dlg = new ChromiumDownloadDialog();
        ShowModal(dlg, owner);
        return dlg.Installed;
    }

    public static Account? ShowAddAccount(AccountStore store, int startTab = 0)
    {
        var dlg = new AddAccountDialog(store, startTab);
        return ShowModal(dlg) == true ? dlg.Added : null;
    }

    public static string? ShowImport()
        => PromptMultiline(L.T("Import.Title"), L.T("Import.Body"), okText: L.T("Import.Action"));

    /// <summary>Native "open file" picker; null when cancelled.</summary>
    public static string? PickFile(string title, string filter = "All files|*.*", string? initialPath = null)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialPath) && System.IO.File.Exists(initialPath))
        {
            dlg.InitialDirectory = System.IO.Path.GetDirectoryName(initialPath);
            dlg.FileName = System.IO.Path.GetFileName(initialPath);
        }
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    /// <summary>Native "save file" picker; null when cancelled.</summary>
    public static string? SaveFile(string title, string filter, string defaultName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultName,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }
}
