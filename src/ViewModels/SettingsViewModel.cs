using System.Collections.ObjectModel;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public class SettingsCategory : ObservableObject
{
    public string Key { get; init; } = "";
    public string IconKey { get; init; } = "";
    public string Title => L.T($"Settings.Cat.{Key}");
    public string Keywords => L.T($"Settings.Cat.{Key}.Keywords");

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One editable colour in the advanced theme editor.</summary>
public class ThemeColorRow : ObservableObject
{
    private readonly Action<string, string> _onChanged;
    public string Key { get; }
    public string Label => L.T($"Theme.Key.{Key}");
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

    public ThemeColorRow(string key, string group, string hex, Action<string, string> onChanged)
    {
        Key = key; Group = group; _hex = hex; _onChanged = onChanged;
    }

    public void SetSilently(string hex)
    {
        _hex = hex;
        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(IsValid));
    }
}

/// <summary>A selectable option with a localized label (combo boxes).</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record AccentChoice(string Name, string Label, string Hex, bool IsSelected);

public class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private AppSettings S => SettingsService.Current;

    public ObservableCollection<SettingsCategory> Categories { get; } = new()
    {
        new() { Key = "General", IconKey = "Icon.Sliders" },
        new() { Key = "Appearance", IconKey = "Icon.Palette" },
        new() { Key = "Launching", IconKey = "Icon.Rocket" },
        new() { Key = "Graphics", IconKey = "Icon.Gauge" },
        new() { Key = "Automation", IconKey = "Icon.Automation" },
        new() { Key = "Hotkeys", IconKey = "Icon.Keyboard" },
        new() { Key = "LiveData", IconKey = "Icon.Activity" },
        new() { Key = "Browser", IconKey = "Icon.Browser" },
        new() { Key = "Network", IconKey = "Icon.Globe" },
        new() { Key = "Security", IconKey = "Icon.Shield" },
        new() { Key = "Notifications", IconKey = "Icon.Bell" },
        new() { Key = "Integrations", IconKey = "Icon.Plug" },
        new() { Key = "Updates", IconKey = "Icon.Download" },
        new() { Key = "Diagnostics", IconKey = "Icon.Info" },
    };

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _selectedCategory = Categories[0];

        SetPasswordCommand = new RelayCommand(_ => SetPassword());
        RemovePasswordCommand = new RelayCommand(_ => RemovePassword());
        LockNowCommand = new RelayCommand(_ => LockService.Lock());
        OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(Paths.DataDir));
        OpenAuditLogCommand = new RelayCommand(_ => OpenFolder(Paths.DataDir));
        DownloadChromiumCommand = new RelayCommand(_ => DownloadChromium());
        RemoveChromiumCommand = new RelayCommand(_ => RemoveChromium());
        OpenPluginsFolderCommand = new RelayCommand(_ => OpenPluginsFolder());
        ReloadPluginsCommand = new RelayCommand(_ => ReloadPlugins());
        ResetThemeCommand = new RelayCommand(_ => ResetTheme());
        SetAccentCommand = new RelayCommand(p => { if (p is string name) AccentName = name; });
        TestWebhookCommand = new RelayCommand(_ => TestWebhook());
        FixMultiInstanceCommand = new RelayCommand(_ => FixMultiInstance());
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated());
        RunHealthCheckCommand = new AsyncRelayCommand(RunHealthCheckAsync);
        OpenDiagnosticsCommand = new RelayCommand(_ => DiagnosticsService.OpenLogFolder());
        CopyDiagnosticsCommand = new RelayCommand(_ =>
        {
            if (ClipboardService.CopyText(DiagnosticsService.BuildReport())) _main.SetStatus(L.T("Diagnostics.Copied"));
        });
        ClearDiagnosticsCommand = new RelayCommand(_ =>
        {
            DiagnosticsService.Clear();
            OnPropertyChanged(nameof(DiagnosticsStatus));
            _main.SetStatus(L.T("Diagnostics.Cleared"));
        });
        TestAntiAfkCommand = new RelayCommand(_ => TestAntiAfk());
        TestBrowserCommand = new AsyncRelayCommand(_ => TestBrowserAsync());
        TrimRamCommand = new RelayCommand(_ => TrimRam());
        CheckForUpdatesCommand = new AsyncRelayCommand(() => _main.CheckForUpdateNowAsync());
        ShowWhatsNewCommand = new AsyncRelayCommand(() => _main.ShowWhatsNewAsync());
        RollbackCommand = new RelayCommand(_ => Rollback(), _ => UpdateService.HasBackup);
        DiscardBackupCommand = new RelayCommand(_ => DiscardBackup(), _ => UpdateService.HasBackup);
        ClearSkippedVersionCommand = new RelayCommand(_ =>
        {
            S.SkippedUpdateVersion = "";
            Persist();
            _main.SetStatus(L.T("Updates.SkipCleared"));
            RefreshUpdates();
        });
        ClearPlaytimeCommand = new RelayCommand(_ => ClearPlaytime());
        ApplyFFlagsNowCommand = new RelayCommand(_ => ApplyFFlagsNow());
        ClearFFlagsCommand = new RelayCommand(_ => ClearFFlags());
        GenerateWebApiTokenCommand = new RelayCommand(_ => { WebApiToken = NewToken(); _main.SetStatus(L.T("Api.TokenGenerated")); });
        CopyWebApiTokenCommand = new RelayCommand(_ => CopyWebApiToken());
        TestProxyCommand = new AsyncRelayCommand(TestProxyAsync);
        ExportBackupCommand = new RelayCommand(_ => ExportBackup());
        ImportBackupCommand = new RelayCommand(_ => ImportBackup());
        ValidateCookiesNowCommand = new AsyncRelayCommand(ValidateCookiesNowAsync);
        OpenUrlCommand = new RelayCommand(p => { if (p is string url) BrowserService.OpenUrl(url); });
        OpenAutomationCommand = new RelayCommand(_ => _main.SelectedIndex = Pages.Automation);

        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.UpdateCheckStatus) or nameof(MainViewModel.UpdateCheckRunning) or "")
            {
                OnPropertyChanged(nameof(UpdateCheckStatus));
                OnPropertyChanged(nameof(UpdateCheckRunning));
            }
        };

        BuildThemeRows();
        BuildHotkeyRows();

        _customFFlagsText = S.CustomFFlags.Count == 0
            ? ""
            : System.Text.Json.JsonSerializer.Serialize(S.CustomFFlags, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        RobloxSingletonService.StatusChanged += RefreshSingletonState;
        RamMonitorService.Sampled += _ => RefreshOnUi(nameof(RamStatus));
        ThemeService.Changed += () => OnPropertyChanged(nameof(Accents));
        LockService.Changed += () => RefreshOnUi(nameof(HasMasterPassword), nameof(PasswordStatus));
    }

    private void RefreshOnUi(params string[] names)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(new Action(() => { foreach (var n in names) OnPropertyChanged(n); }));
    }

    /// <summary>Called whenever the Settings page is shown: re-read everything that can change outside it.</summary>
    public void OnShown()
    {
        OnPropertyChanged(string.Empty);
        foreach (var r in HotkeyRows) r.Refresh();
    }

    public void RefreshLocalized()
    {
        foreach (var c in Categories) c.Refresh();
        foreach (var r in ThemeColors) r.SetSilently(r.Hex);
        foreach (var r in HotkeyRows) r.Refresh();
        ApplySearch();
        OnPropertyChanged(string.Empty);
    }

    private void Persist() { SettingsService.Save(); }

    // ================================================================ navigation + search

    private SettingsCategory _selectedCategory;
    public SettingsCategory SelectedCategory
    {
        get => _selectedCategory;
        set { if (value != null && SetField(ref _selectedCategory, value)) OnPropertyChanged(nameof(SelectedKey)); }
    }

    public string SelectedKey => _selectedCategory.Key;

    private string _search = "";
    public string Search { get => _search; set { if (SetField(ref _search, value ?? "")) ApplySearch(); } }

    private void ApplySearch()
    {
        string q = _search.Trim();
        foreach (var c in Categories)
            c.IsVisible = q.Length == 0
                || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || c.Keywords.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        if (!_selectedCategory.IsVisible && Categories.FirstOrDefault(c => c.IsVisible) is { } first)
            SelectedCategory = first;
    }

    public RelayCommand OpenUrlCommand { get; }
    public RelayCommand OpenAutomationCommand { get; }

    // ================================================================ General

    public IReadOnlyList<Choice> Languages => LocalizationService.Languages.Select(l => new Choice(l.Code, l.NativeName)).ToList();

    public string Language
    {
        get => LocalizationService.Current;
        set
        {
            if (string.IsNullOrEmpty(value) || value == LocalizationService.Current) return;
            S.Language = value;
            Persist();
            LocalizationService.Apply(value);
        }
    }

    public bool MinimizeToTray { get => S.MinimizeToTray; set { S.MinimizeToTray = value; Persist(); OnPropertyChanged(); } }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            // The registry write can be refused on a locked-down machine; reflect what really happened.
            bool ok = StartupService.Set(value);
            S.StartWithWindows = ok && value;
            Persist();
            OnPropertyChanged();
            _main.SetStatus(!ok ? L.T("Startup.Refused") : value ? L.T("Startup.On") : L.T("Startup.Off"));
        }
    }

    public bool StartMinimized { get => S.StartMinimized; set { S.StartMinimized = value; Persist(); OnPropertyChanged(); } }

    // ================================================================ Appearance

    public string ThemeMode
    {
        get => S.ThemeMode;
        set
        {
            if (string.IsNullOrEmpty(value) || value == S.ThemeMode) return;
            S.ThemeMode = value;
            Persist();
            ThemeService.Apply(S);
            RefreshThemeRows();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Accents));
        }
    }

    public void ToggleLightDark() => ThemeMode = ThemeService.IsLight ? ThemeService.ModeDark : ThemeService.ModeLight;

    public IReadOnlyList<AccentChoice> Accents =>
        ThemeService.AccentNames.Select(n => new AccentChoice(n, L.T($"Theme.Accent.{n}"), ThemeService.AccentPreview(n), n == S.AccentName)).ToList();

    public string AccentName
    {
        get => S.AccentName;
        set
        {
            if (string.IsNullOrEmpty(value) || value == S.AccentName) return;
            S.AccentName = value;
            // Accent overrides from the colour editor would hide the new accent — drop just those.
            foreach (var k in new[] { "Accent", "AccentHover", "AccentPressed", "AccentSoft", "OnAccent" })
                S.CustomTheme.Remove(k);
            Persist();
            ThemeService.Apply(S);
            RefreshThemeRows();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Accents));
        }
    }

    public bool IsCompact
    {
        get => _main.Accounts.IsCompact;
        set { _main.Accounts.IsCompact = value; OnPropertyChanged(); }
    }

    public ObservableCollection<ThemeColorRow> ThemeColors { get; } = new();
    public bool HasCustomColors => S.CustomTheme.Count > 0;
    public RelayCommand ResetThemeCommand { get; }
    public RelayCommand SetAccentCommand { get; }

    private void BuildThemeRows()
    {
        var eff = ThemeService.Resolve(S);
        foreach (var (key, group) in ThemeService.Editable)
            ThemeColors.Add(new ThemeColorRow(key, group, eff.TryGetValue(key, out var v) ? v : ThemeService.HexOf(key), OnThemeColorChanged));
    }

    private void RefreshThemeRows()
    {
        var eff = ThemeService.Resolve(S);
        foreach (var row in ThemeColors)
            row.SetSilently(eff.TryGetValue(row.Key, out var v) ? v : ThemeService.HexOf(row.Key));
        OnPropertyChanged(nameof(HasCustomColors));
    }

    private void OnThemeColorChanged(string key, string hex)
    {
        if (string.Equals(ThemeService.BaselineHex(S, key), hex, StringComparison.OrdinalIgnoreCase))
            S.CustomTheme.Remove(key);
        else
            S.CustomTheme[key] = hex;
        ThemeService.Apply(S);
        Persist();
        OnPropertyChanged(nameof(HasCustomColors));
    }

    private void ResetTheme()
    {
        S.CustomTheme = new Dictionary<string, string>();
        ThemeService.Apply(S);
        Persist();
        RefreshThemeRows();
        _main.SetStatus(L.T("Theme.ResetDone"));
    }

    // ================================================================ Launching

    public bool EnableMultiInstance
    {
        get => S.EnableMultiInstance;
        set { S.EnableMultiInstance = value; Persist(); LauncherService.EnsureMultiInstance(value); OnPropertyChanged(); RefreshSingletonState(); }
    }

    public bool CloseSingletonEvent
    {
        get => S.CloseSingletonEvent;
        set { S.CloseSingletonEvent = value; Persist(); RobloxSingletonService.Apply(); OnPropertyChanged(); RefreshSingletonState(); }
    }

    public bool AdoptExternalClients { get => S.AdoptExternalClients; set { S.AdoptExternalClients = value; Persist(); OnPropertyChanged(); } }

    public string MultiInstanceStatus => RobloxSingletonService.StatusText;
    public bool ShowElevatePrompt => RobloxSingletonService.AccessDenied && !RobloxSingletonService.IsElevated;

    public RelayCommand FixMultiInstanceCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }

    private void FixMultiInstance()
    {
        RobloxSingletonService.SweepNow(out string message);
        _main.SetStatus(message);
        RefreshSingletonState();
    }

    private void RestartElevated()
    {
        if (RobloxSingletonService.IsElevated) { _main.SetStatus(L.T("Multi.AlreadyAdmin")); return; }
        if (!DialogService.Confirm(L.T("Multi.Elevate.Title"), L.T("Multi.Elevate.Body"), L.T("Multi.Elevate.Action"))) return;

        _main.Store.Save();
        SettingsService.Save();
        if (RobloxSingletonService.RestartElevated()) System.Windows.Application.Current?.Shutdown();
        else _main.SetStatus(L.T("Multi.Elevate.Cancelled"));
    }

    private void RefreshSingletonState() => RefreshOnUi(nameof(MultiInstanceStatus), nameof(ShowElevatePrompt));

    public bool AutoCloseLastProcess { get => S.AutoCloseLastProcess; set { S.AutoCloseLastProcess = value; Persist(); OnPropertyChanged(); } }
    public int AccountJoinDelay { get => S.AccountJoinDelay; set { S.AccountJoinDelay = Math.Clamp(value, 0, 600); Persist(); OnPropertyChanged(); } }
    public bool ShuffleLowestServer { get => S.ShuffleLowestServer; set { S.ShuffleLowestServer = value; Persist(); OnPropertyChanged(); } }
    public int ShufflePageCount { get => S.ShufflePageCount; set { S.ShufflePageCount = Math.Clamp(value, 1, 25); Persist(); OnPropertyChanged(); } }

    public IReadOnlyList<Choice> FpsCaps => new[] { "0", "30", "60", "75", "120", "144", "165", "240" }
        .Select(v => new Choice(v, v == "0" ? L.T("Graphics.Fps.Default") : L.T("Graphics.Fps.Value", v))).ToList();

    public string FpsCap
    {
        get => S.FpsCap.ToString();
        set
        {
            if (!int.TryParse(value, out int cap)) return;
            S.FpsCap = cap <= 0 ? 0 : Math.Clamp(cap, 30, 1000);
            Persist();
            OnPropertyChanged();
            OnPropertyChanged(nameof(FpsStatus));
        }
    }

    // Ultra-low AFK profile: every part can be switched off on its own.
    private UltraLowOptions Afk => S.UltraLowAfk;
    public int AfkProfileFpsCap { get => Afk.FpsCap; set { Afk.FpsCap = value <= 0 ? 0 : Math.Clamp(value, 5, 1000); Persist(); OnPropertyChanged(); } }
    public bool AfkProfileMinimize { get => Afk.MinimizeWhenInGame; set { Afk.MinimizeWhenInGame = value; Persist(); OnPropertyChanged(); } }
    public bool AfkLowestQuality { get => Afk.LowestQuality; set { Afk.LowestQuality = value; Persist(); OnPropertyChanged(); } }
    public bool AfkNoAntiAliasing { get => Afk.NoAntiAliasing; set { Afk.NoAntiAliasing = value; Persist(); OnPropertyChanged(); } }
    public bool AfkLowestTextures { get => Afk.LowestTextures; set { Afk.LowestTextures = value; Persist(); OnPropertyChanged(); } }
    public bool AfkNoGrass { get => Afk.NoGrass; set { Afk.NoGrass = value; Persist(); OnPropertyChanged(); } }
    public bool AfkGraySky { get => Afk.GraySky; set { Afk.GraySky = value; Persist(); OnPropertyChanged(); } }
    public bool AfkFreezeLighting { get => Afk.FreezeLighting; set { Afk.FreezeLighting = value; Persist(); OnPropertyChanged(); } }

    public string FpsStatus
    {
        get
        {
            var current = RobloxClientSettingsService.ReadFramerateCap();
            if (RobloxClientSettingsService.SettingsFile() == null) return L.T("Graphics.Fps.NoFile");
            return current is { } c ? L.T("Graphics.Fps.Current", c) : L.T("Graphics.Fps.Unknown");
        }
    }

    // ================================================================ Graphics / FastFlags

    public bool ApplyFFlags { get => S.ApplyFFlags; set { S.ApplyFFlags = value; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } }

    public IReadOnlyList<Choice> GraphicsApis => new[] { "Auto", "D3D11", "Vulkan", "OpenGL" }
        .Select(v => new Choice(v, v == "Auto" ? L.T("Common.Automatic") : v switch { "D3D11" => "Direct3D 11", _ => v })).ToList();
    public string GraphicsApi { get => S.GraphicsApi; set { S.GraphicsApi = value ?? "Auto"; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } }

    public IReadOnlyList<Choice> MsaaOptions => new[] { -1, 0, 1, 2, 4, 8 }
        .Select(v => new Choice(v.ToString(), v < 0 ? L.T("Common.Automatic") : v == 0 ? L.T("Common.Off") : $"{v}×")).ToList();
    public string MsaaSamples { get => S.MsaaSamples.ToString(); set { if (int.TryParse(value, out int v)) { S.MsaaSamples = v; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } } }

    public IReadOnlyList<Choice> TextureOptions => new[] { -1, 0, 1, 2, 3 }
        .Select(v => new Choice(v.ToString(), v < 0 ? L.T("Common.Automatic") : L.T($"Graphics.Texture.{v}"))).ToList();
    public string TextureQuality { get => S.TextureQuality.ToString(); set { if (int.TryParse(value, out int v)) { S.TextureQuality = v; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } } }

    public IReadOnlyList<Choice> QualityLevels => Enumerable.Range(0, 22)
        .Select(v => new Choice(v.ToString(), v == 0 ? L.T("Common.Automatic") : v.ToString())).ToList();
    public string QualityLevelOverride { get => S.QualityLevelOverride.ToString(); set { if (int.TryParse(value, out int v)) { S.QualityLevelOverride = v; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } } }

    public bool DisableDpiScale { get => S.DisableDpiScale; set { S.DisableDpiScale = value; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } }
    public bool HideGrass { get => S.HideGrass; set { S.HideGrass = value; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } }
    public bool GraySky { get => S.GraySky; set { S.GraySky = value; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } }
    public bool PauseVoxelizer { get => S.PauseVoxelizer; set { S.PauseVoxelizer = value; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } }
    public bool AltEnterFullscreen { get => S.AltEnterFullscreen; set { S.AltEnterFullscreen = value; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(FFlagsSummary)); } }

    private string _customFFlagsText = "";
    public string CustomFFlagsText
    {
        get => _customFFlagsText;
        set
        {
            if (!SetField(ref _customFFlagsText, value ?? "")) return;
            if (FFlagsService.IsValidRaw(_customFFlagsText))
            {
                S.CustomFFlags = FFlagsService.ParseRaw(_customFFlagsText);
                Persist();
            }
            OnPropertyChanged(nameof(CustomFFlagsValid));
            OnPropertyChanged(nameof(FFlagsSummary));
            OnPropertyChanged(nameof(IgnoredFlagsText));
        }
    }

    public bool CustomFFlagsValid => FFlagsService.IsValidRaw(_customFFlagsText);

    public string IgnoredFlagsText
    {
        get
        {
            if (!CustomFFlagsValid) return L.T("FFlags.InvalidJson");
            var ignored = FFlagsService.NotAllowlisted(S.CustomFFlags.Keys);
            return ignored.Count == 0 ? "" : L.T("FFlags.Ignored", string.Join(", ", ignored.Take(8)) + (ignored.Count > 8 ? "…" : ""));
        }
    }

    public string FFlagsSummary
    {
        get
        {
            int n = FFlagsService.BuildFlags(S).Count;
            return S.ApplyFFlags ? L.N("FFlags.Summary.On", n) : L.N("FFlags.Summary.Off", n);
        }
    }

    public RelayCommand ApplyFFlagsNowCommand { get; }
    public RelayCommand ClearFFlagsCommand { get; }

    private void ApplyFFlagsNow()
    {
        int n = FFlagsService.ApplyForLaunch(S);
        _main.SetStatus(n > 0 ? L.N("FFlags.Written", n) : L.T("FFlags.NothingWritten"));
        OnPropertyChanged(nameof(FpsStatus));
    }

    private void ClearFFlags()
    {
        if (!DialogService.Confirm(L.T("FFlags.Clear.Title"), L.T("FFlags.Clear.Body"), L.T("FFlags.Clear.Action"), danger: true)) return;
        int n = FFlagsService.Clear();
        _main.SetStatus(n > 0 ? L.N("FFlags.Cleared", n) : L.T("FFlags.NoneToClear"));
    }

    // ================================================================ Automation

    public bool AntiAfkEnabled { get => S.AntiAfkEnabled; set { S.AntiAfkEnabled = value; Persist(); AntiAfkService.Apply(); OnPropertyChanged(); } }
    public int AntiAfkIntervalMinutes
    {
        get => S.AntiAfkIntervalMinutes;
        set
        {
            S.AntiAfkIntervalMinutes = Math.Clamp(value, 1, 120);
            S.AntiAfkIntervalMaxMinutes = Math.Max(S.AntiAfkIntervalMaxMinutes, S.AntiAfkIntervalMinutes);
            Persist(); AntiAfkService.Apply(); OnPropertyChanged(); OnPropertyChanged(nameof(AntiAfkIntervalMaxMinutes));
        }
    }
    public bool AntiAfkRandomize { get => S.AntiAfkRandomize; set { S.AntiAfkRandomize = value; Persist(); AntiAfkService.Apply(); OnPropertyChanged(); } }
    public int AntiAfkIntervalMaxMinutes
    {
        get => S.AntiAfkIntervalMaxMinutes;
        set { S.AntiAfkIntervalMaxMinutes = Math.Clamp(value, S.AntiAfkIntervalMinutes, 120); Persist(); AntiAfkService.Apply(); OnPropertyChanged(); }
    }
    public IEnumerable<string> AntiAfkKeys => new[] { "Space", "Shift", "Ctrl", "W", "A", "S", "D", "0", "F13" };
    public string AntiAfkKey { get => S.AntiAfkKey; set { if (!string.IsNullOrEmpty(value)) { S.AntiAfkKey = value; Persist(); OnPropertyChanged(); } } }
    public bool AntiAfkRestoreFocus { get => S.AntiAfkRestoreFocus; set { S.AntiAfkRestoreFocus = value; Persist(); OnPropertyChanged(); } }
    public RelayCommand TestAntiAfkCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public AsyncRelayCommand TestBrowserCommand { get; }

    private void TestAntiAfk()
    {
        int clients = InstanceControlService.Count;
        if (clients == 0) { _main.SetStatus(L.T("AntiAfk.NoClients")); return; }
        AntiAfkService.RunOnce();
        _main.SetStatus(L.N("AntiAfk.Sent", clients));
    }

    public bool WatchdogEnabled { get => S.WatchdogEnabled; set { S.WatchdogEnabled = value; Persist(); WatchdogService.Apply(); OnPropertyChanged(); } }
    public int WatchdogCheckSeconds { get => S.WatchdogCheckSeconds; set { S.WatchdogCheckSeconds = Math.Clamp(value, 5, 600); Persist(); WatchdogService.Apply(); OnPropertyChanged(); } }
    public bool RejoinOnDisconnect { get => S.RejoinOnDisconnect; set { S.RejoinOnDisconnect = value; Persist(); OnPropertyChanged(); } }
    public int DisconnectMinutes { get => S.DisconnectMinutes; set { S.DisconnectMinutes = Math.Clamp(value, 2, 60); Persist(); OnPropertyChanged(); } }
    public bool RestartClientsEnabled { get => S.RestartClientsEnabled; set { S.RestartClientsEnabled = value; Persist(); WatchdogService.Apply(); OnPropertyChanged(); } }
    public int RestartClientsMinutes { get => S.RestartClientsMinutes; set { S.RestartClientsMinutes = Math.Clamp(value, 30, 24 * 60); Persist(); OnPropertyChanged(); } }

    public bool RamMonitorEnabled { get => S.RamMonitorEnabled; set { S.RamMonitorEnabled = value; Persist(); RamMonitorService.Apply(); OnPropertyChanged(); OnPropertyChanged(nameof(RamStatus)); } }
    public int RamMonitorSeconds { get => S.RamMonitorSeconds; set { S.RamMonitorSeconds = Math.Clamp(value, 2, 600); Persist(); RamMonitorService.Apply(); OnPropertyChanged(); } }
    public bool AutoCloseOnHighRam { get => S.AutoCloseOnHighRam; set { S.AutoCloseOnHighRam = value; Persist(); OnPropertyChanged(); } }
    public int RamLimitMb { get => S.RamLimitMb; set { S.RamLimitMb = Math.Clamp(value, 256, 65536); Persist(); OnPropertyChanged(); } }
    public bool AutoTrimEnabled { get => S.AutoTrimEnabled; set { S.AutoTrimEnabled = value; Persist(); RamMonitorService.ApplyAutoTrim(); OnPropertyChanged(); } }
    public int AutoTrimMinutes { get => S.AutoTrimMinutes; set { S.AutoTrimMinutes = Math.Clamp(value, 5, 240); Persist(); RamMonitorService.ApplyAutoTrim(); OnPropertyChanged(); } }
    public RelayCommand TrimRamCommand { get; }

    private void TrimRam()
    {
        var result = RamMonitorService.TrimAll();
        _main.SetStatus(result.Summary);
        OnPropertyChanged(nameof(RamStatus));
    }

    public string RamStatus
    {
        get
        {
            var latest = RamMonitorService.Latest;
            if (latest.Count == 0)
                return S.RamMonitorEnabled ? L.T("Ram.NoClients") : L.T("Ram.Off");
            long total = latest.Sum(x => x.WorkingSetMb);
            var lines = latest.OrderByDescending(x => x.WorkingSetMb).Select(x => $"{x.Alias}   {x.WorkingSetMb:N0} MB");
            return L.N("Ram.Summary", latest.Count, total.ToString("N0")) + "\n" + string.Join("\n", lines);
        }
    }

    // ================================================================ Hotkeys

    public ObservableCollection<HotkeyRow> HotkeyRows { get; } = new();

    private void BuildHotkeyRows()
    {
        foreach (var action in HotkeyBinding.Actions)
        {
            var binding = S.Hotkeys.FirstOrDefault(h => h.Action == action);
            if (binding == null)
            {
                binding = new HotkeyBinding { Action = action };
                S.Hotkeys.Add(binding);
            }
            HotkeyRows.Add(new HotkeyRow(binding, OnHotkeyChanged));
        }
    }

    private void OnHotkeyChanged()
    {
        Persist();
        HotkeyService.Apply();
    }

    public void SetStatusHint(string message) => _main.SetStatus(message);

    // ================================================================ Live data

    /// <summary>Turning presence back on restarts the poll loop right away.</summary>
    public bool ShowPresence
    {
        get => S.ShowPresence;
        set { S.ShowPresence = value; Persist(); if (value) PresenceService.Start(); else PresenceService.Stop(); OnPropertyChanged(); }
    }
    public bool ShowThumbnails { get => S.ShowThumbnails; set { S.ShowThumbnails = value; Persist(); OnPropertyChanged(); } }
    public bool ShowRobux { get => S.ShowRobux; set { S.ShowRobux = value; Persist(); OnPropertyChanged(); } }
    public bool TrackEconomy { get => S.TrackEconomy; set { S.TrackEconomy = value; Persist(); OnPropertyChanged(); } }
    public int PresencePollSeconds { get => S.PresencePollSeconds; set { S.PresencePollSeconds = Math.Clamp(value, 5, 600); Persist(); PresenceService.Start(); OnPropertyChanged(); } }
    public bool TrackPlaytime { get => S.TrackPlaytime; set { S.TrackPlaytime = value; Persist(); OnPropertyChanged(); } }

    public string PlaytimeStatus
    {
        get
        {
            var total = PlaytimeService.AllTimeTotal;
            return total <= TimeSpan.Zero
                ? L.T("Playtime.Nothing")
                : L.T("Playtime.Status", PlaytimeService.Format(total), PlaytimeService.Format(PlaytimeService.Last7DaysTotal));
        }
    }

    public RelayCommand ClearPlaytimeCommand { get; }
    public void RefreshPlaytimeStatus() => OnPropertyChanged(nameof(PlaytimeStatus));

    private void ClearPlaytime()
    {
        if (!DialogService.Confirm(L.T("Playtime.Clear.Title"), L.T("Playtime.Clear.Body"), L.T("Common.Delete"), danger: true)) return;
        _main.SetStatus(PlaytimeService.Clear() ? L.T("Playtime.Cleared") : L.T("Playtime.ClearedFileLocked"));
        _main.RefreshPlaytime();
    }

    // ================================================================ Browser

    public IReadOnlyList<Choice> BrowserEngines => BrowserService.Engines
        .Select(e => new Choice(e, e == BrowserService.EngineAuto ? L.T("Browser.Engine.Auto")
                                  : BrowserService.Find(e) != null ? e
                                  : L.T("Browser.Engine.Missing", e)))
        .ToList();

    public string BrowserEngine
    {
        get => S.BrowserEngine;
        set { if (!string.IsNullOrEmpty(value)) { S.BrowserEngine = value; Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(BrowserStatus)); } }
    }

    private string _browserTestStatus = "";
    public string BrowserTestStatus { get => _browserTestStatus; private set => SetField(ref _browserTestStatus, value); }

    private async Task TestBrowserAsync()
    {
        BrowserTestStatus = L.T("Browser.Test.Running");
        var r = await BrowserService.TestAsync();
        BrowserTestStatus = r.Message;
        _main.SetStatus(r.Message);
    }

    public string BrowserStatus => BrowserService.Resolve() is { } b
        ? L.T("Browser.Status.Using", b.Engine)
        : L.T("Browser.Status.None");

    public bool ChromiumInstalled => ChromiumService.IsInstalled;
    public string ChromiumStatus => ChromiumService.IsInstalled ? L.T("Browser.Cloak.Installed") : L.T("Browser.Cloak.NotInstalled");
    public RelayCommand DownloadChromiumCommand { get; }
    public RelayCommand RemoveChromiumCommand { get; }

    private void DownloadChromium()
    {
        if (DialogService.ShowChromiumDownload()) _main.SetStatus(L.T("Browser.Cloak.Ready"));
        OnPropertyChanged(nameof(ChromiumInstalled));
        OnPropertyChanged(nameof(ChromiumStatus));
        OnPropertyChanged(nameof(BrowserStatus));
        OnPropertyChanged(nameof(BrowserEngines));
    }

    private void RemoveChromium()
    {
        if (!DialogService.Confirm(L.T("Browser.Cloak.Remove.Title"), L.T("Browser.Cloak.Remove.Body"), L.T("Common.Remove"), danger: true)) return;
        _main.SetStatus(ChromiumService.Uninstall() ? L.T("Browser.Cloak.Removed") : L.T("Browser.Cloak.RemoveFailed"));
        OnPropertyChanged(nameof(ChromiumInstalled));
        OnPropertyChanged(nameof(ChromiumStatus));
        OnPropertyChanged(nameof(BrowserStatus));
        OnPropertyChanged(nameof(BrowserEngines));
    }

    // ================================================================ Network

    public bool EnableProxy { get => S.EnableProxy; set { S.EnableProxy = value; Persist(); OnPropertyChanged(); } }
    public string ProxyAddress { get => S.ProxyAddress; set { S.ProxyAddress = (value ?? "").Trim(); Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(ProxyValid)); } }
    public string ProxyUsername { get => S.ProxyUsername; set { S.ProxyUsername = value ?? ""; Persist(); OnPropertyChanged(); } }
    public string ProxyPassword { get => S.ProxyPassword; set { S.ProxyPassword = value ?? ""; Persist(); } }
    public bool ProxyValid => string.IsNullOrWhiteSpace(S.ProxyAddress) || RobloxApi.TryBuildProxy(S.ProxyAddress, null, null) != null;
    public AsyncRelayCommand TestProxyCommand { get; }

    private async Task TestProxyAsync()
    {
        if (string.IsNullOrWhiteSpace(S.ProxyAddress)) { _main.SetStatus(L.T("Proxy.EnterFirst")); return; }
        _main.SetStatus(L.T("Proxy.Testing"));
        var (ok, message) = await RobloxApi.TestProxyAsync(S.ProxyAddress, S.ProxyUsername, S.ProxyPassword);
        _main.SetStatus(message);
        if (ok) ToastService.Success(L.T("Proxy.WorksTitle"), message);
        else ToastService.Error(L.T("Proxy.FailedTitle"), message);
    }

    // ================================================================ Security

    public bool HasMasterPassword => _main.Store.MasterPassword is { Length: > 0 };
    public string PasswordStatus => HasMasterPassword ? L.T("Security.Password.On") : L.T("Security.Password.Off");
    public RelayCommand SetPasswordCommand { get; }
    public RelayCommand RemovePasswordCommand { get; }
    public RelayCommand LockNowCommand { get; }

    private void SetPassword()
    {
        string? pw = DialogService.PromptNewPassword(L.T("Security.Password.SetTitle"), L.T("Security.Password.SetBody"), 8);
        if (pw == null) return;
        _main.Store.SetMasterPassword(pw);
        AuditLogService.Log(AuditLogService.Category.Password, "Master password set or changed");
        _main.SetStatus(L.T("Security.Password.Saved"));
        OnPropertyChanged(nameof(HasMasterPassword));
        OnPropertyChanged(nameof(PasswordStatus));
        _main.RaiseLockState();
    }

    private void RemovePassword()
    {
        if (!HasMasterPassword) return;
        if (!DialogService.Confirm(L.T("Security.Password.RemoveTitle"), L.T("Security.Password.RemoveBody"), L.T("Common.Remove"), danger: true)) return;
        _main.Store.SetMasterPassword(null);
        S.AutoLockEnabled = false;
        S.LockOnMinimize = false;
        Persist();
        AuditLogService.Log(AuditLogService.Category.Password, "Master password removed");
        _main.SetStatus(L.T("Security.Password.Removed"));
        OnPropertyChanged(string.Empty);
        _main.RaiseLockState();
    }

    public bool AutoLockEnabled { get => S.AutoLockEnabled; set { S.AutoLockEnabled = value && HasMasterPassword; Persist(); OnPropertyChanged(); } }
    public int AutoLockMinutes { get => S.AutoLockMinutes; set { S.AutoLockMinutes = Math.Clamp(value, 1, 240); Persist(); OnPropertyChanged(); } }
    public bool LockOnMinimize { get => S.LockOnMinimize; set { S.LockOnMinimize = value && HasMasterPassword; Persist(); OnPropertyChanged(); } }

    public IReadOnlyList<Choice> ClipboardOptions => new[] { 0, 15, 30, 60, 120 }
        .Select(v => new Choice(v.ToString(), v == 0 ? L.T("Common.Never") : L.N("Common.Seconds", v))).ToList();
    public string ClipboardClearSeconds { get => S.ClipboardClearSeconds.ToString(); set { if (int.TryParse(value, out int v)) { S.ClipboardClearSeconds = v; Persist(); OnPropertyChanged(); } } }

    public bool HideUsernames
    {
        get => S.HideUsernames;
        set
        {
            S.HideUsernames = value;
            Persist();
            _main.Accounts.RefreshMask();
            _main.Dashboard.RefreshMask();
            _main.Friends.RefreshMask();
            OnPropertyChanged();
        }
    }

    public bool ValidateCookiesOnStartup { get => S.ValidateCookiesOnStartup; set { S.ValidateCookiesOnStartup = value; Persist(); OnPropertyChanged(); } }
    public bool RotationDetectionEnabled { get => S.RotationDetectionEnabled; set { S.RotationDetectionEnabled = value; Persist(); OnPropertyChanged(); } }
    public bool AuditLogEnabled { get => S.AuditLogEnabled; set { S.AuditLogEnabled = value; Persist(); OnPropertyChanged(); } }
    public RelayCommand OpenAuditLogCommand { get; }
    public AsyncRelayCommand ValidateCookiesNowCommand { get; }

    private async Task ValidateCookiesNowAsync()
    {
        var accounts = _main.Store.Accounts.ToList();
        if (accounts.Count == 0) { _main.SetStatus(L.T("Status.NothingToRefresh")); return; }
        int invalid = await CookieHealthService.ValidateAllAsync(accounts, new Progress<string>(_main.SetStatus));
        _main.Store.Save();
        _main.SetStatus(invalid == 0 ? L.N("Health.AllValid", accounts.Count) : L.N("Health.SomeInvalid", invalid, accounts.Count));
    }

    public RelayCommand ExportBackupCommand { get; }
    public RelayCommand ImportBackupCommand { get; }

    private void ExportBackup()
    {
        var accounts = _main.Store.Accounts.ToList();
        if (accounts.Count == 0) { _main.SetStatus(L.T("Backup.Nothing")); return; }

        // Stricter than the master password: a backup holds raw cookies so it can travel between PCs,
        // which makes this password the only thing between the file and full account takeover.
        const int MinBackupPasswordLength = 12;
        string? password = DialogService.PromptNewPassword(L.T("Backup.Password.Title"), L.T("Backup.Password.Body", MinBackupPasswordLength), MinBackupPasswordLength);
        if (password == null) return;

        string? path = DialogService.SaveFile(L.T("Backup.Save.Title"), "Roblox Account Manager backup (*.rambk)|*.rambk",
            $"accounts-{DateTime.Now:yyyyMMdd-HHmmss}.rambk");
        if (path == null) return;

        try
        {
            BackupService.Export(accounts, path, password);
            _main.SetStatus(L.N("Backup.Done", accounts.Count, System.IO.Path.GetFileName(path)));
            ToastService.Success(L.T("Backup.DoneTitle"), System.IO.Path.GetFileName(path));
            AuditLogService.Log(AuditLogService.Category.Account, $"Encrypted backup written ({accounts.Count} accounts).");
        }
        catch (Exception ex)
        {
            DialogService.Info(L.T("Backup.Failed"), ex.Message);
        }
    }

    private void ImportBackup()
    {
        string? path = DialogService.PickFile(L.T("Backup.Open.Title"), "Roblox Account Manager backup (*.rambk)|*.rambk|*.*|*.*");
        if (path == null) return;

        string? password = DialogService.PromptPassword(L.T("Backup.Unlock.Title"), L.T("Backup.Unlock.Body"));
        if (password == null) return;

        List<Account> restored;
        try { restored = BackupService.Import(path, password); }
        catch (Exception ex)
        {
            DialogService.Info(L.T("Backup.RestoreFailed"), L.T("Backup.RestoreFailedBody", ex.Message));
            return;
        }

        // Merge, never replace: an existing account keeps its place and only gets the fresher cookie.
        int added = 0, refreshed = 0;
        foreach (var r in restored)
        {
            var existing = _main.Store.Accounts.FirstOrDefault(a =>
                (a.UserId != 0 && a.UserId == r.UserId) ||
                (!string.IsNullOrEmpty(r.Username) && string.Equals(a.Username, r.Username, StringComparison.OrdinalIgnoreCase)));

            if (existing != null)
            {
                if (!string.IsNullOrEmpty(r.Cookie) && existing.Cookie != r.Cookie)
                {
                    existing.ReplaceCookie(r.Cookie);
                    existing.UnreadableCookie = null;
                    existing.CookieRejectedUtc = null;
                    refreshed++;
                }
                if (string.IsNullOrEmpty(existing.TotpSecret)) existing.TotpSecret = r.TotpSecret;
            }
            else
            {
                _main.Store.Accounts.Add(r);
                added++;
            }
        }

        _main.Store.Save();
        _main.SetStatus(L.T("Backup.Restored", added, refreshed));
        AuditLogService.Log(AuditLogService.Category.Account, $"Backup restored ({added} added, {refreshed} refreshed).");
        _ = FinishRestoreAsync();
    }

    /// <summary>Validates restored cookies (which backfills ids on very old backups), then loads live data.</summary>
    private async Task FinishRestoreAsync()
    {
        try
        {
            await CookieHealthService.ValidateAllAsync(_main.Store.Accounts.ToList());
            _main.Store.Save();
            await _main.Store.RefreshLiveDataAsync();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("settings", "Post-restore refresh failed", ex);
        }
    }

    // ================================================================ Notifications

    public bool EnableToasts { get => S.EnableToasts; set { S.EnableToasts = value; Persist(); OnPropertyChanged(); } }
    public bool ToastOnLaunch { get => S.ToastOnLaunch; set { S.ToastOnLaunch = value; Persist(); OnPropertyChanged(); } }
    public bool ToastOnCrash { get => S.ToastOnCrash; set { S.ToastOnCrash = value; Persist(); OnPropertyChanged(); } }

    public string DiscordWebhookUrl
    {
        get => S.DiscordWebhookUrl;
        set { S.DiscordWebhookUrl = (value ?? "").Trim(); Persist(); OnPropertyChanged(); OnPropertyChanged(nameof(WebhookValid)); }
    }

    /// <summary>Only Discord's own webhook hosts are accepted, so a typo cannot post account events elsewhere.</summary>
    public bool WebhookValid => string.IsNullOrWhiteSpace(S.DiscordWebhookUrl) || WebhookService.IsDiscordWebhook(S.DiscordWebhookUrl);

    public bool NotifyOnCrash { get => S.NotifyOnCrash; set { S.NotifyOnCrash = value; Persist(); OnPropertyChanged(); } }
    public bool NotifyOnConnect { get => S.NotifyOnConnect; set { S.NotifyOnConnect = value; Persist(); OnPropertyChanged(); } }
    public RelayCommand TestWebhookCommand { get; }

    private void TestWebhook()
    {
        if (!WebhookService.Configured) { _main.SetStatus(L.T("Webhook.EnterFirst")); return; }
        _ = WebhookService.SendEmbedAsync("Webhook test", "Your Roblox Account Manager webhook works.",
            WebhookService.ColorGreen, null, new (string, string)[] { ("Status", "Connected"), ("App", AppInfo.Long) });
        _main.SetStatus(L.T("Webhook.Sent"));
    }

    // ================================================================ Integrations

    public bool WebApiEnabled
    {
        get => S.WebApiEnabled;
        set
        {
            if (value && string.IsNullOrWhiteSpace(S.WebApiToken)) S.WebApiToken = NewToken();
            S.WebApiEnabled = value;
            Persist();
            WebApiService.Apply();
            RefreshWebApi();
        }
    }

    public int WebApiPort { get => S.WebApiPort; set { S.WebApiPort = Math.Clamp(value, 1024, 65535); Persist(); WebApiService.Stop(); WebApiService.Apply(); RefreshWebApi(); } }

    public string WebApiToken
    {
        get => S.WebApiToken;
        set { S.WebApiToken = (value ?? "").Trim(); Persist(); WebApiService.Apply(); RefreshWebApi(); }
    }

    public bool WebApiAllowCookieRead { get => S.WebApiAllowCookieRead; set { S.WebApiAllowCookieRead = value; Persist(); OnPropertyChanged(); } }

    public string WebApiStatus => S.WebApiEnabled && !string.IsNullOrWhiteSpace(S.WebApiToken)
        ? L.T("Api.Status.On", S.WebApiPort)
        : L.T("Api.Status.Off");

    public string WebApiEndpoints => $"GET  /ping\nGET  /accounts\nGET  /status?account=\nPOST /launch?account=&placeId=&jobId=\nPOST /close?account=\nGET  /cookie?account=";

    public RelayCommand GenerateWebApiTokenCommand { get; }
    public RelayCommand CopyWebApiTokenCommand { get; }

    private static string NewToken()
        => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private void RefreshWebApi()
    {
        OnPropertyChanged(nameof(WebApiEnabled));
        OnPropertyChanged(nameof(WebApiToken));
        OnPropertyChanged(nameof(WebApiPort));
        OnPropertyChanged(nameof(WebApiStatus));
    }

    private void CopyWebApiToken()
    {
        if (string.IsNullOrWhiteSpace(S.WebApiToken)) { _main.SetStatus(L.T("Api.NoToken")); return; }
        _main.SetStatus(ClipboardService.CopySecret(S.WebApiToken) ? L.T("Api.TokenCopied") : L.T("Status.ClipboardBusy"));
    }

    public bool EnablePlugins
    {
        get => S.EnablePlugins;
        set
        {
            if (value && !S.EnablePlugins &&
                !DialogService.Confirm(L.T("Plugins.Enable.Title"), L.T("Plugins.Enable.Body"), L.T("Plugins.Enable.Action"), danger: true))
            {
                OnPropertyChanged();
                return;
            }
            S.EnablePlugins = value;
            Persist();
            if (value) PluginService.Load(); else PluginService.Unload();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Plugins));
            OnPropertyChanged(nameof(PluginStatus));
        }
    }

    public IReadOnlyList<PluginService.LoadedPlugin> Plugins => PluginService.Plugins;

    public string PluginStatus
    {
        get
        {
            if (!S.EnablePlugins) return L.T("Plugins.Off");
            var all = PluginService.Plugins;
            if (all.Count == 0) return L.T("Plugins.None");
            int ok = all.Count(p => p.Ok);
            return L.T("Plugins.Status", ok, all.Count - ok);
        }
    }

    public RelayCommand OpenPluginsFolderCommand { get; }
    public RelayCommand ReloadPluginsCommand { get; }

    private void OpenPluginsFolder()
    {
        try { System.IO.Directory.CreateDirectory(PluginService.PluginDir); } catch { }
        OpenFolder(PluginService.PluginDir);
    }

    private void ReloadPlugins()
    {
        // A changed DLL still needs a restart — assemblies loaded with LoadFrom cannot be unloaded.
        try { PluginService.Unload(); PluginService.Load(); }
        catch (Exception ex) { DiagnosticsService.Warn("plugins", "Reload failed", ex); }
        OnPropertyChanged(nameof(Plugins));
        OnPropertyChanged(nameof(PluginStatus));
        _main.SetStatus(PluginStatus);
    }

    // ================================================================ Updates

    public string UpdateCheckStatus => _main.UpdateCheckStatus;
    public bool UpdateCheckRunning => _main.UpdateCheckRunning;
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand ShowWhatsNewCommand { get; }

    public bool AutoCheckUpdates { get => S.AutoCheckUpdates; set { S.AutoCheckUpdates = value; Persist(); _main.ApplyUpdateSchedule(); OnPropertyChanged(); } }
    public bool CheckUpdatesOnStartup { get => S.CheckUpdatesOnStartup; set { S.CheckUpdatesOnStartup = value; Persist(); OnPropertyChanged(); } }
    public int UpdateCheckMinutes { get => S.UpdateCheckMinutes; set { S.UpdateCheckMinutes = Math.Clamp(value, 15, 1440); Persist(); _main.ApplyUpdateSchedule(); OnPropertyChanged(); } }
    public bool IncludePrereleases { get => S.IncludePrereleases; set { S.IncludePrereleases = value; Persist(); OnPropertyChanged(); } }
    public bool VerifyUpdateDownload { get => S.VerifyUpdateDownload; set { S.VerifyUpdateDownload = value; Persist(); OnPropertyChanged(); } }
    public bool KeepUpdateBackup { get => S.KeepUpdateBackup; set { S.KeepUpdateBackup = value; Persist(); OnPropertyChanged(); } }

    public string SkippedVersionText => string.IsNullOrEmpty(S.SkippedUpdateVersion)
        ? L.T("Updates.NoSkip")
        : L.T("Updates.Skipping", S.SkippedUpdateVersion);
    public bool HasSkippedVersion => !string.IsNullOrEmpty(S.SkippedUpdateVersion);
    public RelayCommand ClearSkippedVersionCommand { get; }

    public bool HasUpdateBackup => UpdateService.HasBackup;
    public string BackupStatus => UpdateService.HasBackup
        ? L.T("Updates.Backup.Kept", UpdateService.BackupVersionText ?? L.T("Updates.Backup.Previous"))
        : L.T("Updates.Backup.None");
    public RelayCommand RollbackCommand { get; }
    public RelayCommand DiscardBackupCommand { get; }

    public void RefreshUpdates()
    {
        OnPropertyChanged(nameof(SkippedVersionText));
        OnPropertyChanged(nameof(HasSkippedVersion));
        OnPropertyChanged(nameof(BackupStatus));
        OnPropertyChanged(nameof(HasUpdateBackup));
    }

    private void Rollback()
    {
        if (!UpdateService.HasBackup) return;
        string target = UpdateService.BackupVersionText ?? L.T("Updates.Backup.Previous");
        if (!DialogService.Confirm(L.T("Updates.Rollback.Title", target), L.T("Updates.Rollback.Body", target), L.T("Updates.Rollback.Action")))
            return;
        _main.Store.Save();
        SettingsService.Save();
        if (UpdateService.BeginRollback()) System.Windows.Application.Current?.Shutdown();
        else _main.SetStatus(L.T("Updates.Rollback.Failed"));
    }

    private void DiscardBackup()
    {
        _main.SetStatus(UpdateService.DiscardBackup() ? L.T("Updates.Backup.Deleted") : L.T("Updates.Backup.DeleteFailed"));
        RefreshUpdates();
    }

    // ================================================================ Diagnostics + about

    public ObservableCollection<HealthCheckService.Check> HealthChecks { get; } = new();

    private bool _healthRunning;
    public bool HealthRunning { get => _healthRunning; private set => SetField(ref _healthRunning, value); }

    /// <summary>The manager's own memory use, refreshed whenever Settings opens.</summary>
    public string ManagerFootprint => DiagnosticsService.ManagerFootprint();

    public string DiagnosticsStatus
    {
        get
        {
            int errors = DiagnosticsService.ErrorCount;
            return errors == 0 ? L.T("Diagnostics.NoErrors") : L.N("Diagnostics.Errors", errors);
        }
    }

    public AsyncRelayCommand RunHealthCheckCommand { get; }
    public RelayCommand OpenDiagnosticsCommand { get; }
    public RelayCommand ClearDiagnosticsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    private async Task RunHealthCheckAsync()
    {
        if (HealthRunning) return;
        HealthRunning = true;
        _main.SetStatus(L.T("Diagnostics.Running"));
        try
        {
            var results = await HealthCheckService.RunAsync();
            HealthChecks.Clear();
            foreach (var c in results) HealthChecks.Add(c);
            int failed = results.Count(c => !c.Ok);
            _main.SetStatus(failed == 0 ? L.T("Diagnostics.Passed") : L.N("Diagnostics.Failed", failed));
        }
        finally
        {
            HealthRunning = false;
            OnPropertyChanged(nameof(DiagnosticsStatus));
        }
    }

    public string AppVersion => AppInfo.Long;
    public string DataFolder => Paths.DataDir;

    public string InstallStatus
    {
        get
        {
            string? install = RobloxInstallService.DescribeInstall();
            string? owner = RobloxInstallService.ProtocolOwner();
            if (install == null) return L.T("Diagnostics.NoRoblox");
            return owner != null && !owner.Contains("roblox", StringComparison.OrdinalIgnoreCase)
                ? install + "\n" + L.T("Diagnostics.HandledBy", owner)
                : install;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })) { }
        }
        catch (Exception ex) { DiagnosticsService.Warn("settings", "Folder could not be opened", ex); }
    }
}

/// <summary>
/// One global hotkey. The chord itself is recorded by the settings page (a key press is a view
/// concern); this wrapper persists it and re-registers the hotkeys.
/// </summary>
public class HotkeyRow : ObservableObject
{
    private readonly HotkeyBinding _binding;
    private readonly Action _onChanged;

    public string Action => _binding.Action;
    public string Label => _binding.ActionLabel;

    public HotkeyRow(HotkeyBinding binding, Action onChanged)
    {
        _binding = binding;
        _onChanged = onChanged;
    }

    public bool Enabled
    {
        get => _binding.Enabled;
        set { if (_binding.Enabled == value) return; _binding.Enabled = value; OnPropertyChanged(); _onChanged(); }
    }

    public string ChordText => _binding.ChordText;

    private bool _capturing;
    public bool Capturing
    {
        get => _capturing;
        set { if (SetField(ref _capturing, value)) OnPropertyChanged(nameof(DisplayText)); }
    }

    public string DisplayText => _capturing ? L.T("Hotkeys.Press") : ChordText;

    public void SetChord(uint modifiers, uint key)
    {
        _binding.Modifiers = modifiers;
        _binding.Key = key;
        Capturing = false;
        OnPropertyChanged(nameof(ChordText));
        OnPropertyChanged(nameof(DisplayText));
        _onChanged();
    }

    public void CancelCapture() => Capturing = false;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
