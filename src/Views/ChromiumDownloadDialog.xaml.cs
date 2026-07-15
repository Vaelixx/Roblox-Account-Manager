using System.Threading;
using System.Windows;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

public partial class ChromiumDownloadDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    public bool Installed { get; private set; }

    public ChromiumDownloadDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<ChromiumService.Progress>(Report);
        try
        {
            await ChromiumService.DownloadAsync(progress, _cts.Token);
            Installed = ChromiumService.IsInstalled;
            DialogResult = Installed;
            Close();
        }
        catch (System.OperationCanceledException)
        {
            DialogResult = false;
            Close();
        }
        catch (System.Exception ex)
        {
            PhaseText.Text = "Failed: " + ex.Message;
            CancelBtn.Content = "Close";
        }
    }

    private void Report(ChromiumService.Progress p)
    {
        PhaseText.Text = p.Phase;
        if (p.Total > 0)
        {
            Fill.Width = p.Fraction * Track.ActualWidth;
            SizeText.Text = $"{p.Done / 1024 / 1024} / {p.Total / 1024 / 1024} MB";
        }
        else if (p.Phase == "Ready")
        {
            Fill.Width = Track.ActualWidth;
            SizeText.Text = "";
        }
        else
        {
            SizeText.Text = "";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        DialogResult = false;
        Close();
    }
}
