using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
        // Contract: --apply-update "<mainExePath>" <mainPid> "<downloadUrl>" "<versionText>"
        // Runs as the %TEMP% updater copy: show only the updater window, skip everything else
        // (including the single-instance mutex — the main app is still shutting down).
        if (e.Args.Length >= 5 && e.Args[0] == "--apply-update")
        {
            DispatcherUnhandledException += OnUnhandledException;
            base.OnStartup(e);

            var updater = new UpdaterWindow(e.Args[1], e.Args[2], e.Args[3], e.Args[4]);
            MainWindow = updater;
            updater.Show();
            return;
        }

        // Contract: --post-update "<tempDir>" — normal startup, plus background temp-dir cleanup.
        string? updateTempDir = e.Args.Length >= 2 && e.Args[0] == "--post-update" ? e.Args[1] : null;

        _instanceMutex = new Mutex(true, "RobloxAccountManager.Modern.SingleInstance", out bool isNew);

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
            // forward it to the live instance and exit quietly; otherwise just surface it.
            if (CliService.HasActionableArgs(e.Args) && SingleInstanceService.TrySendToPrimary(e.Args))
            {
                Shutdown();
                return;
            }
            MessageBox.Show("Roblox Account Manager is already running.", "Roblox Account Manager",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // Central diagnostics sink. Installed before anything else runs so the very first
        // failure — a corrupt settings file, an unreadable data folder — is already recorded.
        // It also owns the unobserved-task hook that used to live inline here, so fire-and-forget
        // pollers land in one rotating, cookie-scrubbed log instead of an ever-growing error.log.
        DiagnosticsService.Install();

        base.OnStartup(e);

        SettingsService.Load();
        ThemeService.Apply(SettingsService.Current);   // paint the saved palette before the window shows
        LocalizationService.Apply(SettingsService.Current.Language);  // publish Str.* for the chosen language

        var vm = new MainViewModel();
        if (!LoadAccounts(vm.Store))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();

        WireBackgroundServices(vm);

        // Listen for CLI requests forwarded by later instances (e.g. `RAM.exe --launch …`).
        SingleInstanceService.StartServer(a => _ = CliService.HandleAsync(vm, a));

        // Honour a CLI launch that started *this* (primary) instance, now that accounts are ready.
        if (CliService.HasActionableArgs(e.Args))
            _ = CliService.HandleAsync(vm, e.Args);

        if (updateTempDir != null)
            _ = Task.Run(() => CleanupUpdateDirAsync(updateTempDir));

        // Privacy sweep: remove app-browser profiles a crash or forced shutdown left behind,
        // so no cookie/site data from a previous session stays readable on disk.
        _ = Task.Run(BrowserService.CleanupLeftoverProfiles);
    }

    /// <summary>
    /// Starts the background engines (crash watchdog, anti-AFK) and runs an optional
    /// startup cookie-health sweep. Called once the main window is up so status updates
    /// have somewhere to land.
    /// </summary>
    private static void WireBackgroundServices(MainViewModel vm)
    {
        // Multi-instance guard. Started here — not only on the launch path — because the whole
        // point is that clients we never launch (website Play button, Roblox home screen,
        // Discord invite) also need Roblox's per-client singleton lock cleared.
        RobloxSingletonService.Apply();

        WatchdogService.Init(userId => vm.Store.Accounts.FirstOrDefault(a => a.UserId == userId));
        WatchdogService.Apply();
        AntiAfkService.Apply();

        // Scheduler resolves a task's stored account by alias first, then username,
        // so renaming the display name never breaks a saved schedule.
        SchedulerService.Init(key => vm.Store.Accounts.FirstOrDefault(a =>
            string.Equals(a.Alias,    key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Username, key, StringComparison.OrdinalIgnoreCase)));
        SchedulerService.Start();

        RamMonitorService.Apply();

        // Local control server snapshots the live account collection per request.
        WebApiService.Init(() => vm.Store.Accounts.ToList());
        WebApiService.Apply();

        // Third-party plugins: give the host a live account snapshot, then load DLLs
        // from the plugins folder. A throwing plugin is isolated inside the loader.
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
            _ = CookieHealthService.ValidateAllAsync(vm.Store.Accounts.ToList());

        // Global hotkeys: each enabled chord fires a named power-tool action. The
        // dispatch runs on the UI thread (the hotkey window rides the WPF pump), so
        // touching the view-model here is safe.
        HotkeyService.Init(action => DispatchHotkey(vm, action));
        HotkeyService.Apply();
    }

    /// <summary>Routes a global-hotkey action id to the matching power-tool.</summary>
    private static void DispatchHotkey(MainViewModel vm, string action)
    {
        switch (action)
        {
            case "LaunchSelected":
                vm.Accounts.LaunchCommand.Execute(null);
                break;

            case "ServerHopSelected":
                vm.Accounts.ServerHopCommand.Execute(null);
                break;

            case "CloseAllRoblox":
                int n = LauncherService.CloseAllClients();
                vm.SetStatus(n > 0 ? $"Closed {n} Roblox client(s)." : "No Roblox clients were running.");
                break;

            case "FocusManager":
                if (Current?.MainWindow is { } w)
                {
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Show();
                    w.Activate();
                    w.Topmost = true;   // nudge past other windows…
                    w.Topmost = false;  // …without actually pinning it there
                    w.Focus();
                }
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
                DialogService.Info("Account file could not be read",
                    "Your saved accounts could not be decrypted. That usually means the file was "
                    + "copied from a different Windows user or PC — DPAPI encryption is tied to the "
                    + "account that wrote it.\n\n"
                    + "Nothing has been changed and nothing was deleted. Your file is in:\n"
                    + Paths.DataDir + "\n\n"
                    + "The app will close now so the file stays intact.");
                return false;
            }
            return true;
        }

        // Password-protected: prompt until correct or the user cancels.
        while (true)
        {
            string? pw = DialogService.PromptPassword("Unlock account file",
                "This account file is protected with a master password.");
            if (pw == null) return false; // cancelled -> exit app

            if (store.Load(pw)) return true;

            DialogService.Info("Incorrect password", "That password didn't work. Please try again.");
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Append, never overwrite: the old code truncated error.log on every crash, so a repeating
        // fault destroyed the evidence of the first one.
        DiagnosticsService.Error("ui", "Unhandled dispatcher exception", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails were written to:\n{DiagnosticsService.LogPath}",
            "Roblox Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstanceService.StopServer();
        AntiAfkService.Stop();
        WatchdogService.Stop();
        SchedulerService.Stop();
        RamMonitorService.Stop();
        WebApiService.Stop();
        PresenceService.Stop();
        HotkeyService.Stop();
        PluginService.Unload(); // let plugins release timers/sockets (may still Close() clients)
        LauncherService.ReleaseMultiInstance();
        BrowserService.CleanupLeftoverProfiles(); // still-open browsers keep locks; next start re-sweeps
        base.OnExit(e);
    }
}
