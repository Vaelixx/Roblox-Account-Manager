using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RobloxAccountManager.Mvvm;

namespace RobloxAccountManager.Services
{
    public enum ToastKind { Info, Success, Warning, Error }

    /// <summary>A single in-app toast. Immutable content; carries its own dismiss
    /// command so the overlay template can close it without a view-model.</summary>
    public sealed class ToastItem : ObservableObject
    {
        public string Title { get; }
        public string Message { get; }
        public ToastKind Kind { get; }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

        /// <summary>DynamicResource brush key for the accent bar / title colour.</summary>
        public string AccentKey => Kind switch
        {
            ToastKind.Success => "SuccessBrush",
            ToastKind.Warning => "WarningBrush",
            ToastKind.Error   => "DangerBrush",
            _                 => "AccentBrush",
        };

        public RelayCommand DismissCommand { get; }

        public ToastItem(string title, string message, ToastKind kind)
        {
            Title = title ?? "";
            Message = message ?? "";
            Kind = kind;
            DismissCommand = new RelayCommand(() => ToastService.Dismiss(this));
        }
    }

    /// <summary>
    /// Lightweight in-app notification host. Toasts are added to <see cref="Items"/>,
    /// which the main-window overlay binds to, and auto-expire after a few seconds.
    /// Every entry point respects the user's <c>EnableToasts</c> preference and marshals
    /// onto the UI thread, so services may call it from any background context.
    /// </summary>
    public static class ToastService
    {
        public static ObservableCollection<ToastItem> Items { get; } = new();

        /// <summary>Seconds a toast stays before it auto-dismisses.</summary>
        public static int DurationSeconds { get; set; } = 5;

        /// <summary>Never show more than this many at once (oldest drops off).</summary>
        private const int MaxVisible = 5;

        public static void Info(string title, string message = "")    => Show(title, message, ToastKind.Info);
        public static void Success(string title, string message = "") => Show(title, message, ToastKind.Success);
        public static void Warning(string title, string message = "") => Show(title, message, ToastKind.Warning);
        public static void Error(string title, string message = "")   => Show(title, message, ToastKind.Error);

        public static void Show(string title, string message, ToastKind kind)
        {
            // Honour the master switch. Individual event gates (launch/crash) are the
            // caller's responsibility; this is the last-line opt-out.
            if (!SettingsService.Current.EnableToasts) return;

            var app = Application.Current;
            if (app?.Dispatcher == null) return;   // headless / early startup — silently skip

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                var item = new ToastItem(title, message, kind);
                Items.Add(item);
                while (Items.Count > MaxVisible) Items.RemoveAt(0);

                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(Math.Max(2, DurationSeconds))
                };
                timer.Tick += (_, __) =>
                {
                    timer.Stop();
                    Items.Remove(item);
                };
                timer.Start();
            }));
        }

        public static void Dismiss(ToastItem item)
        {
            if (item == null) return;
            var app = Application.Current;
            if (app?.Dispatcher == null) { Items.Remove(item); return; }
            app.Dispatcher.Invoke(() => Items.Remove(item));
        }
    }
}
