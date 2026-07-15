using System.Windows;
using System.Windows.Input;

namespace RobloxAccountManager.Views;

public partial class MessageDialog : Window
{
    public enum Kind { Confirm, Text, Multiline, Password }

    private readonly Kind _kind;

    public string ResultText { get; private set; } = "";

    public MessageDialog(Kind kind, string title, string message, string initial = "",
        string okText = "OK", bool showCancel = true, string cancelText = "Cancel")
    {
        InitializeComponent();
        _kind = kind;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        OkBtn.Content = okText;
        CancelBtn.Content = cancelText;
        CancelBtn.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        switch (kind)
        {
            case Kind.Confirm:
                InputHost.Visibility = Visibility.Collapsed;
                break;
            case Kind.Text:
                Input.Style = (System.Windows.Style)FindResource(typeof(System.Windows.Controls.TextBox));
                Input.Text = initial;
                Input.MinHeight = 38;
                Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
                break;
            case Kind.Multiline:
                Input.Text = initial;
                Input.Height = 150;           // fixed height + internal scroll (never grows the window)
                Input.MinHeight = 150;
                Input.MaxHeight = 150;
                Loaded += (_, _) => Input.Focus();
                break;
            case Kind.Password:
                Input.Visibility = Visibility.Collapsed;
                Password.Visibility = Visibility.Visible;
                Loaded += (_, _) => Password.Focus();
                break;
        }

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultText = _kind == Kind.Password ? Password.Password : Input.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
