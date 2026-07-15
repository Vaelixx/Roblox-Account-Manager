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
        if (!isNew)
        {
            MessageBox.Show("Roblox Account Manager is already running.", "Roblox Account Manager",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        SettingsService.Load();

        var vm = new MainViewModel();
        if (!LoadAccounts(vm.Store))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();

        if (updateTempDir != null)
            _ = Task.Run(() => CleanupUpdateDirAsync(updateTempDir));
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
            store.Load(null);
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
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "error.log"), e.Exception.ToString()); } catch { }
        MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}", "Roblox Account Manager",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LauncherService.ReleaseMultiInstance();
        base.OnExit(e);
    }
}
