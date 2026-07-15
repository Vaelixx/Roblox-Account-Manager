using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

public partial class AddAccountDialog : Window
{
    private readonly AccountStore _store;
    private bool _working;

    public Account? Added { get; private set; }

    public AddAccountDialog(AccountStore store)
    {
        InitializeComponent();
        _store = store;
        Loaded += (_, _) => CookieBox.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_working) { DialogResult = false; Close(); } };
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                CookieBox.Text = Clipboard.GetText().Trim();
                CookieBox.CaretIndex = CookieBox.Text.Length;
            }
        }
        catch { }
    }

    private void Cookie_Changed(object sender, TextChangedEventArgs e)
    {
        // Clear any prior error as soon as the user edits.
        if (!_working) StatusBar.Visibility = Visibility.Collapsed;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        string cookie = CookieBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cookie))
        {
            ShowStatus("Paste a cookie first.", isError: true);
            return;
        }

        SetWorking(true);
        ShowStatus("Validating with Roblox…", isError: false, spinner: true);

        var result = await _store.AddByCookieAsync(cookie);

        if (result.Account != null)
        {
            Added = result.Account;
            ShowStatus(result.Message, isError: false, success: true);
            await Task.Delay(650);
            DialogResult = true;
            Close();
            return;
        }

        SetWorking(false);
        ShowStatus(result.Message, isError: true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        DialogResult = false;
        Close();
    }

    private void SetWorking(bool working)
    {
        _working = working;
        AddBtn.IsEnabled = !working;
        CancelBtn.IsEnabled = !working;
        CookieBox.IsEnabled = !working;
        PasteBtn.IsEnabled = !working;
        AddBtn.Content = working ? "Adding…" : "Add account";
    }

    private void ShowStatus(string text, bool isError, bool success = false, bool spinner = false)
    {
        StatusBar.Visibility = Visibility.Visible;
        StatusText.Text = text;

        Color accent;
        if (success) accent = (Color)ColorConverter.ConvertFromString("#3FB950");
        else if (isError) accent = (Color)ColorConverter.ConvertFromString("#F85149");
        else accent = (Color)ColorConverter.ConvertFromString("#7B61FF");

        StatusText.Foreground = new SolidColorBrush(accent);
        StatusIcon.Stroke = new SolidColorBrush(accent);
        StatusBar.Background = new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B));

        string key = success ? "Icon.Check" : isError ? "Icon.Close" : "Icon.Refresh";
        StatusIcon.Data = (Geometry)FindResource(key);
    }
}
