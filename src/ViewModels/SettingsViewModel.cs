using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private AppSettings S => SettingsService.Current;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        SetPasswordCommand = new RelayCommand(_ => SetPassword());
        RemovePasswordCommand = new RelayCommand(_ => RemovePassword());
        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());
        DownloadChromiumCommand = new RelayCommand(_ => DownloadChromium());
    }

    // Browser
    public bool ChromiumInstalled => ChromiumService.IsInstalled;
    public string ChromiumStatus => ChromiumService.IsInstalled
        ? "Private Chromium is installed — accounts open in it, separate from your normal browser."
        : "Not installed yet. \"Open in browser\" will download a portable Chromium (~330 MB) on first use.";
    public string ChromiumButtonText => ChromiumService.IsInstalled ? "Re-download Chromium" : "Download Chromium";
    public RelayCommand DownloadChromiumCommand { get; }

    private void DownloadChromium()
    {
        if (DialogService.ShowChromiumDownload())
            _main.SetStatus("Chromium ready.");
        RefreshChromium();
    }

    /// <summary>Re-reads Chromium install state (called when the Settings page becomes visible).</summary>
    public void RefreshChromium()
    {
        OnPropertyChanged(nameof(ChromiumInstalled));
        OnPropertyChanged(nameof(ChromiumStatus));
        OnPropertyChanged(nameof(ChromiumButtonText));
    }

    // Launch
    public bool EnableMultiInstance
    {
        get => S.EnableMultiInstance;
        set { S.EnableMultiInstance = value; Persist(); LauncherService.EnsureMultiInstance(value); }
    }
    public bool AutoCloseLastProcess { get => S.AutoCloseLastProcess; set { S.AutoCloseLastProcess = value; Persist(); } }
    public int AccountJoinDelay { get => S.AccountJoinDelay; set { S.AccountJoinDelay = value; Persist(); } }
    public bool ShuffleLowestServer { get => S.ShuffleLowestServer; set { S.ShuffleLowestServer = value; Persist(); } }
    public int ShufflePageCount { get => S.ShufflePageCount; set { S.ShufflePageCount = Math.Clamp(value, 1, 25); Persist(); } }

    // FPS
    public bool UnlockFps { get => S.UnlockFps; set { S.UnlockFps = value; Persist(); } }
    public int MaxFps { get => S.MaxFps; set { S.MaxFps = Math.Clamp(value, 30, 1000); Persist(); } }

    // Live data
    public bool ShowPresence { get => S.ShowPresence; set { S.ShowPresence = value; Persist(); } }
    public bool ShowThumbnails { get => S.ShowThumbnails; set { S.ShowThumbnails = value; Persist(); } }
    public bool ShowRobux { get => S.ShowRobux; set { S.ShowRobux = value; Persist(); } }

    // Interface
    public bool HideUsernames { get => S.HideUsernames; set { S.HideUsernames = value; Persist(); _main.Accounts.RefreshMask(); } }
    public bool MinimizeToTray { get => S.MinimizeToTray; set { S.MinimizeToTray = value; Persist(); } }

    // Security
    public bool HasMasterPassword => _main.Store.MasterPassword is { Length: > 0 };
    public string PasswordStatus => HasMasterPassword
        ? "Your account file is encrypted with a master password."
        : "Your account file is encrypted with Windows DPAPI (tied to your Windows user).";

    public RelayCommand SetPasswordCommand { get; }
    public RelayCommand RemovePasswordCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    public string AppVersion => "Version 1.0.0";

    private void Persist() { SettingsService.Save(); OnPropertyChanged(""); }

    private void SetPassword()
    {
        string? pw = DialogService.PromptPassword("Set master password",
            "Choose a password (4+ characters). You'll need it every time you open the app.");
        if (pw == null) return;
        if (pw.Length < 4) { _main.SetStatus("Password must be at least 4 characters."); return; }
        _main.Store.SetMasterPassword(pw);
        _main.SetStatus("Master password set.");
        OnPropertyChanged(nameof(HasMasterPassword));
        OnPropertyChanged(nameof(PasswordStatus));
    }

    private void RemovePassword()
    {
        if (!HasMasterPassword) return;
        if (!DialogService.Confirm("Remove master password",
            "The account file will fall back to Windows DPAPI encryption. Continue?")) return;
        _main.Store.SetMasterPassword(null);
        _main.SetStatus("Master password removed.");
        OnPropertyChanged(nameof(HasMasterPassword));
        OnPropertyChanged(nameof(PasswordStatus));
    }

    private void OpenDataFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Paths.DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.DataDir) { UseShellExecute = true });
        }
        catch { }
    }
}
