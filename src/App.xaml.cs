using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;
using RobloxAccountManager.ViewModels;
using RobloxAccountManager.Views;

namespace RobloxAccountManager;

public partial class App : Application
{
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- self-update entry points (parsed BEFORE any normal startup work) ----
        // Contract: --apply-update "<mainExe>" <mainPid> "<url>" "<version>"
        //                          [<size> "<sha256|->" <verify> <keepBackup>]      (v1.7.0+)
        //                          [<language> <Light|Dark> <accent>]                 (v2.0.0+)
        // Every trailing group stays optional, so an older build handing over to a newer updater
        // (or the reverse) still produces a working update.
        // Runs as the %TEMP% updater copy: show only the updater window, skip everything else
        // (including the single-instance mutex — the main app is still shutting down).
        if (e.Args.Length >= 5 && e.Args[0] == "--apply-update")
        {
            DispatcherUnhandledException += OnUnhandledException;
            base.OnStartup(e);

            string? Arg(int i) => e.Args.Length > i ? e.Args[i] : null;

            // The updater copy cannot read the settings file (it runs from %TEMP%), so it is told how to look.
            LocalizationService.Apply(LocalizationService.ResolveInitial(Arg(9)));
            ThemeService.Apply(new AppSettings
            {
                ThemeMode = Arg(10) is ThemeService.ModeLight ? ThemeService.ModeLight : ThemeService.ModeDark,
                AccentName = ThemeService.AccentNames.Contains(Arg(11)) ? Arg(11)! : ThemeService.AccentNames[0],
            });

            var updater = new UpdaterWindow(e.Args[1], e.Args[2], e.Args[3], e.Args[4],
                                            Arg(5), Arg(6), Arg(7), Arg(8));
            MainWindow = updater;
            updater.Show();
            return;
        }

        // Contract: --post-update "<tempDir>" — normal startup, plus background temp-dir cleanup.
        string? updateTempDir = e.Args.Length >= 2 && e.Args[0] == "--post-update" ? e.Args[1] : null;

#if DEBUG
        // Debug builds only: a throw-away data folder with sample accounts, for screenshots and UI work.
        DemoMode.Configure(e.Args);
#endif

        _instanceMutex = new Mutex(true, AppInfo.IsDemo ? "RobloxAccountManager.Demo." + Environment.ProcessId
                                                        : "RobloxAccountManager.Modern.SingleInstance", out bool isNew);

        // "Restart as administrator" hands over to this elevated copy while the old one is still
        // tearing down. Without a grace period the handover would greet the user with
        // "already running" and leave them un-elevated — the exact state they tried to escape.
        if (!isNew && e.Args.Contains("--restart", StringComparer.OrdinalIgnoreCase))
        {
            for (int attempt = 0; attempt < 25 && !isNew; attempt++)
            {
                Thread.Sleep(400);
                try { _instanceMutex.Dispose(); } catch { }
                _instanceMutex = new Mutex(true, "RobloxAccountManager.Modern.SingleInstance", out isNew);
            }
        }

        if (!isNew)
        {
            // Already running. If we were started to perform an action (e.g. --launch),
            // forward it to the live instance and exit quietly; otherwise bring its window back
            // (from the tray too). Only when it can't be reached is the user told to look for it.
            bool actionable = CliService.HasActionableArgs(e.Args);
            string[] forward = actionable ? e.Args : new[] { SingleInstanceService.ShowFlag };
            if (SingleInstanceService.TrySendToPrimary(forward, legacyFallback: actionable))
            {
                Shutdown();
                return;
            }
            LocalizationService.Apply(LocalizationService.ResolveInitial(null));
            MessageBox.Show(L.T("App.AlreadyRunning"), "Roblox Account Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _isPrimary = true;
        DispatcherUnhandledException += OnUnhandledException;

        // Central diagnostics sink. Installed before anything else runs so the very first
        // failure — a corrupt settings file, an unreadable data folder — is already recorded.
        DiagnosticsService.Install();
        if (DataFolderMigration.LastResult is { } migrated) DiagnosticsService.Warn("data", migrated);

        base.OnStartup(e);

        // Until the main window exists, closing a window must not end the app. WPF makes the first
        // window it creates the MainWindow — here the unlock prompt — and with OnMainWindowClose,
        // closing that prompt started the shutdown: every window after it (the "wrong password"
        // message, the next prompt, even the main window) was silently never shown, and the
        // process sat in the background without a window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Listen for a second start from the beginning, so one made while the unlock prompt is open
        // brings that prompt forward instead of claiming the manager is in the tray.
        SingleInstanceService.StartServer(OnForwarded);

        try
        {
            StartUp(e.Args, updateTempDir);
        }
        catch (Exception ex)
        {
            // With the explicit shutdown mode a failure here would otherwise leave a process without a
            // window that keeps the single-instance lock, so every later start says "already running".
            DiagnosticsService.Error("app", "Startup failed", ex);
            MessageBox.Show(L.T("App.UnexpectedError", ex.Message, DiagnosticsService.LogPath),
                "Roblox Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // Set once this copy owns the single-instance lock and runs the app (not a second start that only
    // hands over, and not the updater copy).
    private static bool _isPrimary;

    // Launch requests a second start forwarded before the accounts were open; run once they are.
    private static MainViewModel? _readyVm;
    private static readonly List<string[]> _pendingCli = new();

    private void StartUp(string[] args, string? updateTempDir)
    {
        SettingsService.Load();
#if DEBUG
        DemoMode.AdjustSettings(SettingsService.Current);
#endif
        LocalizationService.Apply(LocalizationService.ResolveInitial(SettingsService.Current.Language));
        ThemeService.Apply(SettingsService.Current);   // paint the saved palette before the window shows

        var vm = new MainViewModel();
        if (!LoadAccounts(vm.Store))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(vm);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

#if DEBUG
        if (AppInfo.IsDemo)
        {
            DemoMode.Populate(vm);
            DemoMode.RunScript(window, vm);
            return;
        }
#endif

        // Before hiding to the tray: "lock when hidden" needs the lock to know the store, and the
        // rest of the background services only start below.
        LockService.Init(vm.Store);

        // Autostart with "start minimized": Show() first regardless — the tray icon is created in
        // OnSourceInitialized, which only runs once the window has a handle. Hiding straight after
        // gives a tray-only start without a window ever flashing up.
        if (SettingsService.Current.StartMinimized
            && args.Contains(StartupService.StartupArg, StringComparer.OrdinalIgnoreCase))
            window.HideToTray();

        WireBackgroundServices(vm);

        // Update check and "what's new" only now: a prompt over the unlock dialog could start an
        // update before the accounts were even read.
        vm.StartAfterUnlock();

        _readyVm = vm;
        foreach (var queued in _pendingCli) _ = CliService.HandleAsync(vm, queued);
        _pendingCli.Clear();

        // Honour a CLI launch that started *this* (primary) instance, now that accounts are ready.
        if (CliService.HasActionableArgs(args))
            _ = CliService.HandleAsync(vm, args);

        if (updateTempDir != null)
            _ = Task.Run(() => CleanupUpdateDirAsync(updateTempDir));

        // Privacy sweep: remove app-browser profiles a crash or forced shutdown left behind,
        // so no cookie/site data from a previous session stays readable on disk.
        _ = Task.Run(BrowserService.CleanupLeftoverProfiles);
    }

    /// <summary>
    /// A request from a later start of the exe (on the UI thread): a plain second start brings the
    /// window back, also from the tray; a CLI launch runs, or waits until the accounts are open.
    /// </summary>
    private static void OnForwarded(string[] args)
    {
        if (args.Length == 1 && args[0] == SingleInstanceService.ShowFlag)
        {
            if (Current?.MainWindow is MainWindow main) main.BringToFront();
            else Current?.MainWindow?.Activate();   // still at the unlock prompt
            return;
        }
        if (_readyVm is { } vm) _ = CliService.HandleAsync(vm, args);
        else _pendingCli.Add(args);
    }

    /// <summary>
    /// Starts the background engines (crash watchdog, anti-AFK, scheduler, …) and runs an optional
    /// startup cookie-health sweep. Called once the main window is up so status updates have
    /// somewhere to land.
    /// </summary>
    private static void WireBackgroundServices(MainViewModel vm)
    {
        // Web requests made for an account go through that account's own proxy when it has one.
        RobloxApi.AccountProxyResolver = vm.Store.ProxyForCookie;

        // Multi-instance guard. Started here — not only on the launch path — because the whole
        // point is that clients we never launch (website Play button, Roblox home screen,
        // Discord invite) also need Roblox's per-client singleton lock cleared.
        RobloxSingletonService.Apply();

        WatchdogService.Init(userId => vm.Store.Accounts.FirstOrDefault(a => a.UserId == userId));
        WatchdogService.Apply();
        AntiAfkService.Apply();

        // Presets and schedules resolve a stored account by alias first, then username, so renaming
        // the display name never breaks a saved preset or task.
        Account? Resolve(string key) => vm.Store.Accounts.FirstOrDefault(a =>
            string.Equals(a.Alias, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Username, key, StringComparison.OrdinalIgnoreCase));
        PresetService.Init(Resolve);
        SchedulerService.Init(Resolve);
        SchedulerService.Start();

        RamMonitorService.Apply();

        // Local control server snapshots the live account collection per request.
        WebApiService.Init(() => vm.Store.Accounts.ToList());
        WebApiService.Apply();

        // Third-party plugins load only when they were switched on in Settings.
        PluginService.Init(() => vm.Store.Accounts.ToList());
        PluginService.Load();

        // Launcher shares the store so a .ROBLOSECURITY rotated by Roblox during the
        // auth-ticket call can be written back to the account and persisted to disk.
        LauncherService.Init(vm.Store);

        // Live dashboard: poll presence on a short background cadence so the status
        // counters stay current without the heavier thumbnail/robux refresh path.
        PresenceService.Init(vm.Store);
        PresenceService.Start();

        if (SettingsService.Current.ValidateCookiesOnStartup)
            _ = ValidateOnStartupAsync(vm);

        // Global hotkeys: each enabled chord fires a named action. The dispatch runs on the UI
        // thread (the hotkey window rides the WPF pump), so touching the view-model here is safe.
        HotkeyService.Init(action => DispatchHotkey(vm, action));
        HotkeyService.Apply();
    }

    private static async Task ValidateOnStartupAsync(MainViewModel vm)
    {
        try
        {
            await CookieHealthService.ValidateAllAsync(vm.Store.Accounts.ToList());
            vm.Store.Save();   // persists the validation dates the health panel shows
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("health", "Startup session check failed", ex);
        }
    }

    /// <summary>Routes a global-hotkey action id to the matching action.</summary>
    private static void DispatchHotkey(MainViewModel vm, string action)
    {
        // A locked manager only answers the hotkeys that cannot reveal or start anything.
        if (LockService.IsLocked && action is not ("FocusManager" or "LockManager")) return;

        switch (action)
        {
            case "LaunchSelected":
                vm.Accounts.LaunchCommand.Execute(null);
                break;

            case "ServerHopSelected":
                vm.Accounts.ServerHopCommand.Execute(null);
                break;

            case "CloseAllRoblox":
                vm.CloseAllClients();
                break;

            case "LockManager":
                if (!LockService.Lock("hotkey")) vm.SetStatus(L.T("Lock.NeedPassword"));
                break;

            case "FocusManager":
                if (Current?.MainWindow is MainWindow w) w.BringToFront();
                break;
        }
    }

    /// <summary>Best-effort removal of the %TEMP% updater folder left behind by --apply-update.</summary>
    private static async Task CleanupUpdateDirAsync(string dir)
    {
        try
        {
            // Only ever delete inside %TEMP%, no matter what was passed on the command line.
            // Trailing-separator match: rejects prefix collisions (…\Temperature) and %TEMP% itself.
            string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dir));
            string temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (!full.StartsWith(temp + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (!System.IO.Directory.Exists(full)) return;
                    System.IO.Directory.Delete(full, recursive: true);
                    return;
                }
                catch { } // Updater.exe is usually still exiting — wait and retry.
                await Task.Delay(500);
            }
        }
        catch { }
    }

    /// <summary>Loads the account store, prompting for the master password if the file needs one.</summary>
    private static bool LoadAccounts(AccountStore store)
    {
        if (!store.StoreExists)
        {
            store.Load(null);
            return true;
        }

        if (!store.IsPasswordProtected)
        {
            // A store that exists but fails to decrypt must NOT fall through as "no accounts".
            // The app would come up empty and the first Save() — which OnClosing does
            // unconditionally — would overwrite the real file and its backup with an empty one.
            // Refuse to start instead, so the file survives to be recovered.
            if (!store.Load(null))
            {
                DialogService.Info(L.T("Startup.Unreadable.Title"), L.T("Startup.Unreadable.Body", Paths.DataDir));
                return false;
            }
            if (store.LooksEmptiedByUpdate) OfferBackupRestore(store);
            return true;
        }

        // Password-protected: one prompt that stays open until the password is right or the user
        // cancels (which exits the app). A wrong password is reported inside the prompt.
        int failures = 0;
        string? pw = DialogService.PromptPassword(L.T("Startup.Unlock.Title"), L.T("Startup.Unlock.Body"), L.T("Lock.Unlock"),
            async candidate =>
            {
                if (store.Load(candidate)) return null;

                // Slow down guessing a little more with every wrong password.
                failures++;
                if (failures >= 3) await Task.Delay(Math.Min(10, failures) * 400);
                return L.T("Startup.WrongPassword.Body");
            });
        return pw != null;
    }

    /// <summary>
    /// In 2.0, pressing "Update now" in the prompt that appeared over the unlock dialog saved the
    /// never-opened store: accounts.dat became an empty file without a master password and the real
    /// one survived only as accounts.bak, which the next save would overwrite. Bring it back before
    /// anything saves, or, if the user declines, move it aside untouched.
    /// </summary>
    private static void OfferBackupRestore(AccountStore store)
    {
        string? pw = DialogService.PromptPassword(L.T("Startup.Recover.Title"), L.T("Startup.Recover.Body"), L.T("Startup.Recover.Action"),
            candidate => Task.FromResult(store.TryRestoreBackup(candidate) ? null : L.T("Lock.Wrong")));
        if (pw != null)
        {
            DiagnosticsService.Warn("store", $"Restored {store.Accounts.Count} account(s) from the password-protected backup");
            return;
        }
        string? kept = store.KeepBackupAside();
        DiagnosticsService.Warn("store", $"Password-protected account backup not restored; kept as {kept ?? "accounts.bak (could not be moved)"}");
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticsService.Error("ui", "Unhandled dispatcher exception", e.Exception);
        MessageBox.Show(L.T("App.UnexpectedError", e.Exception.Message, DiagnosticsService.LogPath),
            "Roblox Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // A second start that only handed over to the running copy, and the updater copy, never
        // started anything. Tearing down here wrote their empty playtime history over the real one
        // and wiped the running copy's browser profiles.
        if (AppInfo.IsDemo || !_isPrimary) { base.OnExit(e); return; }

        SingleInstanceService.StopServer();
        // Book every client still running: once this process is gone nothing observes them, and
        // for a manager left open all day that is most of the playtime there is to record.
        PlaytimeService.FlushOpenSessions();
        AntiAfkService.Stop();
        WatchdogService.Stop();
        SchedulerService.Stop();
        RamMonitorService.Stop();
        WebApiService.Stop();
        PresenceService.Stop();
        HotkeyService.Stop();
        LockService.Stop();
        PluginService.Unload(); // let plugins release timers/sockets (may still Close() clients)
        LauncherService.ReleaseMultiInstance();
        ClipboardService.ClearSecretIfPresent();
        BrowserService.CleanupLeftoverProfiles(); // still-open browsers keep locks; next start re-sweeps

        // Hand the single-instance lock back now rather than whenever the process is torn down, so a
        // start right after this one closes doesn't see "already running".
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
