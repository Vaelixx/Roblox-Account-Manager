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
