using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>One editable swatch in the theme editor. Setting <see cref="Hex"/>
/// writes the override into settings and repaints the app live.</summary>
public class ThemeColorRow : ObservableObject
{
    private readonly System.Action<string, string> _onChanged;
    public string Key { get; }
    public string Label { get; }
    public string Group { get; }

    private string _hex;
    public string Hex
    {
        get => _hex;
        set
        {
            var v = (value ?? "").Trim();
            if (v == _hex) return;
            _hex = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsValid));
            if (ThemeService.TryColor(v, out _)) _onChanged(Key, v);
        }
    }

    public bool IsValid => ThemeService.TryColor(_hex, out _);

    public ThemeColorRow(string key, string label, string group, string hex,
                         System.Action<string, string> onChanged)
    {
        Key = key; Label = label; Group = group; _hex = hex; _onChanged = onChanged;
    }

    /// <summary>Silently updates the shown value without firing the change hook
    /// (used when a preset switch rewrites the whole palette).</summary>
    public void SetSilently(string hex)
    {
        _hex = hex;
        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(IsValid));
    }
}

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
        OpenPluginsFolderCommand = new RelayCommand(_ => OpenPluginsFolder());
        ReloadPluginsCommand = new RelayCommand(_ => ReloadPlugins());
        ResetThemeCommand = new RelayCommand(_ => ResetTheme());
        BuildThemeRows();
    }

    // Browser
    public bool ChromiumInstalled => ChromiumService.IsInstalled;
    public string ChromiumStatus => ChromiumService.IsInstalled
        ? "CloakBrowser is installed — accounts open in it, separate from your normal browser."
        : "Not installed yet. \"Open in browser\" will download a portable CloakBrowser (~540 MB) on first use.";
    public string ChromiumButtonText => ChromiumService.IsInstalled ? "Re-download CloakBrowser" : "Download CloakBrowser";
    public RelayCommand DownloadChromiumCommand { get; }

    private void DownloadChromium()
    {
        if (DialogService.ShowChromiumDownload())
            _main.SetStatus("CloakBrowser ready.");
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

    // Plugins
    public System.Collections.Generic.IReadOnlyList<PluginService.LoadedPlugin> Plugins => PluginService.Plugins;
    public string PluginStatus
    {
        get
        {
            var all = PluginService.Plugins;
            if (all.Count == 0) return "No plugins loaded. Drop a plugin DLL into the plugins folder and reload.";
            int ok = all.Count(p => p.Ok), bad = all.Count - ok;
            return bad == 0 ? $"{ok} plugin(s) loaded." : $"{ok} loaded, {bad} failed.";
        }
    }
    public RelayCommand OpenPluginsFolderCommand { get; }
    public RelayCommand ReloadPluginsCommand { get; }

    public string AppVersion => AppInfo.Long;

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

    private void OpenPluginsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(PluginService.PluginDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PluginService.PluginDir) { UseShellExecute = true });
        }
        catch { }
    }

    private void ReloadPlugins()
    {
        // Re-scans the plugins folder and re-runs OnLoad. Note: Assembly.LoadFrom cannot truly
        // unload an assembly, so newly-added DLLs are picked up, but a changed DLL needs an app restart.
        try { PluginService.Unload(); PluginService.Load(); }
        catch { }
        OnPropertyChanged(nameof(Plugins));
        OnPropertyChanged(nameof(PluginStatus));
        _main.SetStatus(PluginStatus);
    }

    // ---- Appearance / Theme editor (#30 UX-Politur) ----

    /// <summary>The built-in preset names, for the picker.</summary>
    public IEnumerable<string> ThemePresets => ThemeService.Presets.Keys;

    /// <summary>Selected preset. Switching rewrites the custom overrides to the
    /// preset's colours, repaints live, and refreshes the editable swatches.</summary>
    public string ThemeName
    {
        get => S.ThemeName;
        set
        {
            if (value == null || value == S.ThemeName) return;
            S.ThemeName = value;
            // Adopt the preset as the new editable baseline so the swatches match
            // what is on screen and further edits layer cleanly on top.
            S.CustomTheme = ThemeService.Presets.TryGetValue(value, out var p)
                ? new Dictionary<string, string>(p)
                : new Dictionary<string, string>();
            ThemeService.Apply(S);
            SettingsService.Save();
            RefreshThemeRows();
            OnPropertyChanged();
        }
    }

    /// <summary>Editable colour swatches (label + hex), grouped for display.</summary>
    public ObservableCollection<ThemeColorRow> ThemeColors { get; } = new();

    public RelayCommand ResetThemeCommand { get; }

    private void BuildThemeRows()
    {
        var eff = ThemeService.Resolve(S);
        foreach (var (key, label, group) in ThemeService.Editable)
        {
            var hex = eff.TryGetValue(key, out var v) ? v : BaselineHex(key);
            ThemeColors.Add(new ThemeColorRow(key, label, group, hex, OnThemeColorChanged));
        }
    }

    private void RefreshThemeRows()
    {
        var eff = ThemeService.Resolve(S);
        foreach (var row in ThemeColors)
            row.SetSilently(eff.TryGetValue(row.Key, out var v) ? v : BaselineHex(row.Key));
    }

    /// <summary>Reads the current live colour so a fresh row shows the real value
    /// even for keys the user has never overridden.</summary>
    private static string BaselineHex(string key)
    {
        var app = System.Windows.Application.Current;
        if (app?.Resources[key] is System.Windows.Media.Color c)
            return c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        return "#000000";
    }

    private void OnThemeColorChanged(string key, string hex)
    {
        S.CustomTheme ??= new Dictionary<string, string>();
        S.CustomTheme[key] = hex;
        ThemeService.Apply(S);
        SettingsService.Save();
    }

    private void ResetTheme()
    {
        S.ThemeName = "Avallon (mono)";
        S.CustomTheme = new Dictionary<string, string>();
        ThemeService.Apply(S);
        SettingsService.Save();
        RefreshThemeRows();
        OnPropertyChanged(nameof(ThemeName));
    }

    // ---- Views / i18n / Notifications ----
    public IEnumerable<string> ViewModes => new[] { "Card", "Compact" };
    public string AccountViewMode
    {
        get => S.AccountViewMode;
        set { if (value != null && value != S.AccountViewMode) { S.AccountViewMode = value; Persist(); _main.Accounts.RefreshViewMode(); } }
    }

    public IEnumerable<string> Languages => LocalizationService.Languages.Select(l => l.Label);
    public string Language
    {
        get => LocalizationService.LabelFor(S.Language);
        set
        {
            var code = LocalizationService.CodeFor(value);
            if (code == S.Language) return;
            S.Language = code;
            LocalizationService.Apply(code);
            Persist();
        }
    }

    public bool EnableToasts { get => S.EnableToasts; set { S.EnableToasts = value; Persist(); } }
    public bool ToastOnLaunch { get => S.ToastOnLaunch; set { S.ToastOnLaunch = value; Persist(); } }
    public bool ToastOnCrash { get => S.ToastOnCrash; set { S.ToastOnCrash = value; Persist(); } }
}
