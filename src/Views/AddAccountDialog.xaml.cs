using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

/// <summary>
/// Adds an account in one of three ways:
///
/// <list type="bullet">
/// <item><b>Browser</b> (default) — Roblox's own login page opens in a clean, private browser window and
/// the session cookie is read once the user is signed in. Captchas, two-step verification, passkeys and
/// whatever Roblox adds next keep working because it is Roblox's real page; the password never passes
/// through this app.</item>
/// <item><b>Password</b> — username and password are posted to Roblox's login API. Quick when Roblox does
/// not ask for a captcha; when it does, the dialog points the user to the browser tab.</item>
/// <item><b>Cookie</b> — paste a <c>.ROBLOSECURITY</c> value the user already has.</item>
/// </list>
/// </summary>
public partial class AddAccountDialog : Window
{
    private enum Mode { Browser = 0, Password = 1, Cookie = 2 }

    private readonly AccountStore _store;
    private bool _working;
    private Mode _mode;

    /// <summary>Set once Roblox asks for a two-step code; the next submit answers it.</summary>
    private RobloxAuthService.TwoStepChallenge? _challenge;

    /// <summary>Non-null while a browser sign-in is running, so Cancel stops it.</summary>
    private CancellationTokenSource? _browserCts;

    private Storyboard? _spin;

    public Account? Added { get; private set; }

    public AddAccountDialog(AccountStore store, int startTab = 0)
    {
        InitializeComponent();
        _store = store;
        _mode = Enum.IsDefined(typeof(Mode), startTab) ? (Mode)startTab : Mode.Browser;

        (_mode switch { Mode.Password => PasswordTab, Mode.Cookie => CookieTab, _ => BrowserTab }).IsChecked = true;
        ApplyMode();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel_Click(this, new RoutedEventArgs()); }
        };
        Closing += (_, e) =>
        {
            // Closing the window mid sign-in stops the browser wait instead of leaving it orphaned.
            if (_browserCts != null) { try { _browserCts.Cancel(); } catch { } }
        };
    }

    // ---------------------------------------------------------------
    //  Mode switch
    // ---------------------------------------------------------------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || BrowserPanel == null) return;
        var mode = sender == PasswordTab ? Mode.Password : sender == CookieTab ? Mode.Cookie : Mode.Browser;
        if (mode == _mode) return;
        _mode = mode;
        ApplyMode();
    }

    private void ApplyMode()
    {
        BrowserPanel.Visibility = _mode == Mode.Browser ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = _mode == Mode.Password ? Visibility.Visible : Visibility.Collapsed;
        CookiePanel.Visibility = _mode == Mode.Cookie ? Visibility.Visible : Visibility.Collapsed;

        // Leaving the password tab abandons any half-finished two-step attempt.
        if (_mode != Mode.Password) ClearTwoFactor();

        if (_mode == Mode.Browser) ApplyBrowserInfo();

        StatusBar.Visibility = Visibility.Collapsed;
        UpdateActionButton();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_mode == Mode.Password) (UserBox.Text.Length == 0 ? (IInputElement)UserBox : PassBox).Focus();
            else if (_mode == Mode.Cookie) CookieBox.Focus();
            else ActionBtn.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void UpdateActionButton()
    {
        ActionBtn.Content = _mode switch
        {
            Mode.Browser => L.T("Add.Action.Browser"),
            Mode.Cookie => L.T("Add.Action.Cookie"),
            _ => _challenge != null ? L.T("Add.Action.Verify") : L.T("Add.Action.SignIn"),
        };
    }

    // ---------------------------------------------------------------
    //  Input
    // ---------------------------------------------------------------

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            CookieBox.Text = Clipboard.GetText().Trim();
            CookieBox.CaretIndex = CookieBox.Text.Length;
        }
        catch { }
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => ClearStatus();

    private void Password_Changed(object sender, RoutedEventArgs e) => ClearStatus();

    private void ClearStatus()
    {
        if (!_working) StatusBar.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------
    //  Submit
    // ---------------------------------------------------------------

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        try
        {
            switch (_mode)
            {
                case Mode.Browser: await BrowserSignInAsync(); break;
                case Mode.Cookie: await AddByCookieAsync(); break;
                default:
                    if (_challenge != null) await SubmitTwoFactorAsync();
                    else await SignInAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("add-account", "Adding an account failed", ex);
            SetWorking(false);
            ShowStatus(L.T("Common.SomethingWentWrong") + " " + ex.Message, Tone.Error);
        }
    }

    private async Task AddByCookieAsync()
    {
        string cookie = CookieBox.Text.Trim();
        if (cookie.Length == 0) { ShowStatus(L.T("Add.Cookie.Empty"), Tone.Error); CookieBox.Focus(); return; }

        SetWorking(true);
        ShowStatus(L.T("Add.Validating"), Tone.Busy);
        await StoreCookieAsync(cookie);
    }

    private async Task SignInAsync()
    {
        string user = UserBox.Text.Trim();
        string pass = PassBox.Password;

        if (user.Length == 0) { ShowStatus(L.T("Auth.NeedUsername"), Tone.Error); UserBox.Focus(); return; }
        if (pass.Length == 0) { ShowStatus(L.T("Auth.NeedPassword"), Tone.Error); PassBox.Focus(); return; }

        SetWorking(true);
        ShowStatus(L.T("Add.SigningIn"), Tone.Busy);

        var result = await RobloxAuthService.LoginAsync(user, pass);
        await HandleLoginResultAsync(result);
    }

    private async Task SubmitTwoFactorAsync()
    {
        var challenge = _challenge;
        if (challenge == null) return;

        string code = CodeBox.Text.Trim();
        if (code.Length == 0) { ShowStatus(L.T("Auth.NeedCode"), Tone.Error); CodeBox.Focus(); return; }

        SetWorking(true);
        ShowStatus(L.T("Add.CheckingCode"), Tone.Busy);

        var result = await RobloxAuthService.CompleteTwoStepAsync(UserBox.Text.Trim(), PassBox.Password, challenge, code);
        await HandleLoginResultAsync(result);
    }

    private async Task HandleLoginResultAsync(RobloxAuthService.LoginResult result)
    {
        // A two-step prompt is news the first time and a rejected code the second time.
        bool codeWasRejected = _challenge != null;

        switch (result.Outcome)
        {
            case RobloxAuthService.LoginOutcome.Success when result.Cookie != null:
                ShowStatus(L.T("Add.SignedInAdding"), Tone.Busy);
                await StoreCookieAsync(result.Cookie);
                return;

            case RobloxAuthService.LoginOutcome.TwoStepRequired when result.TwoStep != null:
                _challenge = result.TwoStep;
                TwoFactorPanel.Visibility = Visibility.Visible;
                TwoFactorLabel.Text = L.T("Add.CodeFrom", result.TwoStep.SourceText);
                SetWorking(false);
                ShowStatus(result.Message, codeWasRejected ? Tone.Error : Tone.Info);
                CodeBox.Focus();
                CodeBox.SelectAll();
                return;

            case RobloxAuthService.LoginOutcome.ChallengeRequired:
                // Roblox wants a captcha — only a real browser can show it.
                ClearTwoFactor();
                SetWorking(false);
                ShowStatus(L.T("Add.UseBrowserInstead"), Tone.Error);
                return;

            default:
                // A rejected code keeps the challenge so the next code can be tried.
                if (result.Outcome != RobloxAuthService.LoginOutcome.TwoStepRequired) ClearTwoFactor();
                SetWorking(false);
                ShowStatus(result.Message, Tone.Error);
                return;
        }
    }

    /// <summary>Validates the cookie, stores the account and closes on success.</summary>
    private async Task StoreCookieAsync(string cookie)
    {
        var result = await _store.AddByCookieAsync(cookie);

        if (result.Account != null)
        {
            Added = result.Account;
            ShowStatus(result.Message, Tone.Success);
            await Task.Delay(550);
            if (IsLoaded) DialogResult = true;
            return;
        }

        SetWorking(false);
        ShowStatus(result.Message, Tone.Error);
    }

    // ---------------------------------------------------------------
    //  Browser sign-in
    // ---------------------------------------------------------------

    private async Task BrowserSignInAsync()
    {
        SetWorking(true);
        CancelBtn.IsEnabled = true;          // Cancel stops the wait instead of closing the dialog
        CancelBtn.Content = L.T("Common.Stop");
        ShowStatus(L.T("Browser.Login.Opening", BrowserService.Resolve()?.Engine ?? "Chromium"), Tone.Busy);

        _browserCts = new CancellationTokenSource();
        var progress = new Progress<string>(text => { if (_working) ShowStatus(text, Tone.Busy); });

        BrowserService.LoginCapture capture;
        try
        {
            capture = await BrowserService.CaptureLoginCookieAsync(progress, _browserCts.Token);

            // No Edge / Chrome on this PC: offer the private browser once, then try again.
            if (!capture.Success && capture.NoBrowser)
            {
                ShowStatus(L.T("Add.Browser.NeedDownload"), Tone.Info);
                if (DialogService.ShowChromiumDownload(this) && !_browserCts.IsCancellationRequested)
                    capture = await BrowserService.CaptureLoginCookieAsync(progress, _browserCts.Token);
            }
        }
        finally
        {
            _browserCts.Dispose();
            _browserCts = null;
            CancelBtn.Content = L.T("Common.Cancel");
        }

        if (!IsLoaded) return;

        if (capture.Success && capture.Cookie != null)
        {
            Activate();
            ShowStatus(L.T("Add.SignedInAdding"), Tone.Busy);
            await StoreCookieAsync(capture.Cookie);
            return;
        }

        SetWorking(false);
        ShowStatus(capture.Message, Tone.Error);
        ApplyBrowserInfo();
    }

    private void ApplyBrowserInfo()
    {
        var browser = BrowserService.Resolve();
        BrowserInfo.Text = browser != null ? L.T("Add.Browser.Uses", browser.Engine) : L.T("Add.Browser.NoneYet");
    }

    private void ClearTwoFactor()
    {
        _challenge = null;
        CodeBox.Text = "";
        TwoFactorPanel.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------
    //  Chrome
    // ---------------------------------------------------------------

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // While a browser sign-in is open, Cancel means "stop waiting", not "close the dialog".
        if (_browserCts != null)
        {
            try { _browserCts.Cancel(); } catch { }
            return;
        }

        if (_working) return;
        DialogResult = false;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void SetWorking(bool working)
    {
        _working = working;
        ActionBtn.IsEnabled = !working;
        CancelBtn.IsEnabled = !working;
        BrowserTab.IsEnabled = !working;
        PasswordTab.IsEnabled = !working;
        CookieTab.IsEnabled = !working;
        UserBox.IsEnabled = !working;
        PassBox.IsEnabled = !working;
        CodeBox.IsEnabled = !working;
        CookieBox.IsEnabled = !working;
        PasteBtn.IsEnabled = !working;
        UpdateActionButton();
    }

    private enum Tone { Busy, Info, Success, Error }

    private void ShowStatus(string text, Tone tone)
    {
        if (string.IsNullOrWhiteSpace(text)) { StatusBar.Visibility = Visibility.Collapsed; return; }

        StatusBar.Visibility = Visibility.Visible;
        StatusText.Text = text;

        string fg = tone switch { Tone.Success => "SuccessBrush", Tone.Error => "DangerBrush", _ => "TextPrimaryBrush" };
        string bg = tone switch { Tone.Success => "SuccessSoftBrush", Tone.Error => "DangerSoftBrush", _ => "SurfaceAltBrush" };
        string icon = tone switch
        {
            Tone.Success => "Icon.CheckCircle",
            Tone.Error => "Icon.AlertCircle",
            Tone.Info => "Icon.Info",
            _ => "Icon.Refresh",
        };

        StatusText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        StatusIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, tone is Tone.Busy or Tone.Info ? "TextSecondaryBrush" : fg);
        StatusBar.SetResourceReference(Border.BackgroundProperty, bg);
        StatusIcon.Data = (Geometry)FindResource(icon);

        if (tone == Tone.Busy) StartSpin(); else StopSpin();
    }

    private void StartSpin()
    {
        if (_spin != null) return;
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(anim, StatusIcon);
        Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.Angle"));
        _spin = new Storyboard();
        _spin.Children.Add(anim);
        _spin.Begin(this, true);
    }

    private void StopSpin()
    {
        if (_spin == null) return;
        _spin.Stop(this);
        _spin = null;
        StatusSpin.Angle = 0;
    }
}
