using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using BeeperWinUI.Services;

namespace BeeperWinUI.Views;

public sealed partial class SetupPage : Page
{
    public SetupPage()
    {
        this.InitializeComponent();

        SendCodeBtn.Click += (s, e) => _ = SendCodeAsync();
        VerifyBtn.Click += (s, e) => _ = VerifyCodeAsync();
        BackToEmailBtn.Click += (s, e) => ShowEmailStep();
        VerifyDeviceBtn.Click += (s, e) => _ = StartDeviceVerificationAsync();
        ConfirmEmojiBtn.Click += (s, e) => _ = ConfirmVerificationAsync();
        CancelVerifyBtn.Click += (s, e) => CancelVerificationUI();
        SkipRecoveryBtn.Click += (s, e) => FinishAndShowShell();

        EmailBox.KeyDown += (s, e) => { if (e.Key == Windows.System.VirtualKey.Enter) _ = SendCodeAsync(); };
        CodeBox.KeyDown += (s, e) => { if (e.Key == Windows.System.VirtualKey.Enter) _ = VerifyCodeAsync(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var savedEmail = App.Settings.BeeperEmail;
        if (!string.IsNullOrEmpty(savedEmail))
            EmailBox.Text = savedEmail;

        var savedToken = App.Settings.AccessToken;
        if (!string.IsNullOrEmpty(savedToken))
            _ = AutoConnectAsync(savedToken);
    }

    private async Task SendCodeAsync()
    {
        var email = EmailBox.Text.Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            ErrorBar.Message = "Please enter a valid email address.";
            ErrorBar.IsOpen = true;
            return;
        }

        ErrorBar.IsOpen = false;
        SendCodeBtn.IsEnabled = false;
        ShowProgress("Starting and sending code...");

        if (!App.IsSidecarRunning)
        {
            var started = App.StartSidecar(App.Settings.SidecarPort);
            if (!started)
            {
                ErrorBar.Message = "Could not start sidecar. Make sure beeper-sidecar.exe is built.";
                ErrorBar.IsOpen = true;
                HideProgress();
                SendCodeBtn.IsEnabled = true;
                return;
            }
            await Task.Delay(1500);
        }

        BeeperApiService.SetBaseUrl(App.Settings.BaseUrl);

        var status = await App.Api.AuthStatusAsync();
        if (status?.LoggedIn == true)
        {
            var localToken = App.Settings.AccessToken;
            if (!string.IsNullOrEmpty(localToken))
            {
                App.Api.SetToken(localToken);
                App.Settings.SaveSession(App.Settings.BaseUrl, localToken, status.UserID ?? "sidecar", null);
                HideProgress();
                ((App)Application.Current).ShowShell();
                return;
            }
        }

        var result = await App.Api.AuthLoginAsync(email);
        if (result == null)
        {
            var errMsg = App.Api.LastError ?? "Could not reach sidecar";
            if (errMsg.Length > 200) errMsg = errMsg[..200] + "...";
            ErrorBar.Message = $"Login failed: {errMsg}";
            ErrorBar.IsOpen = true;
            HideProgress();
            SendCodeBtn.IsEnabled = true;
            return;
        }

        App.Settings.BeeperEmail = email;
        HideProgress();
        StatusBar.Message = result.Message ?? "Check your email for a verification code.";
        StatusBar.IsOpen = true;
        ShowCodeStep(email);
    }

    private void ShowEmailStep()
    {
        EmailPanel.Visibility = Visibility.Visible;
        CodePanel.Visibility = Visibility.Collapsed;
        SendCodeBtn.IsEnabled = true;
        StatusBar.IsOpen = false;
        ErrorBar.IsOpen = false;
    }

    private void ShowCodeStep(string email)
    {
        EmailPanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Visible;
        CodePromptText.Text = $"Enter the code sent to {email}";
        CodeBox.Text = "";
        VerifyBtn.IsEnabled = true;
        CodeBox.Focus(FocusState.Programmatic);
    }

    private async Task VerifyCodeAsync()
    {
        var code = CodeBox.Text.Trim();
        var email = App.Settings.BeeperEmail ?? EmailBox.Text.Trim();

        if (string.IsNullOrEmpty(code))
        {
            ErrorBar.Message = "Please enter the verification code.";
            ErrorBar.IsOpen = true;
            return;
        }

        ErrorBar.IsOpen = false;
        VerifyBtn.IsEnabled = false;
        ShowProgress("Verifying...");

        var result = await App.Api.AuthVerifyAsync(email, code);
        if (result == null || result.Status != "authenticated")
        {
            var errMsg = App.Api.LastError ?? "invalid code";
            if (errMsg.Length > 200) errMsg = errMsg[..200] + "...";
            ErrorBar.Message = $"Verification failed: {errMsg}";
            ErrorBar.IsOpen = true;
            HideProgress();
            VerifyBtn.IsEnabled = true;
            return;
        }

        var localToken = result.AccessToken ?? "";
        App.Api.SetToken(localToken);
        App.Settings.SaveSession(
            result.HomeserverURL ?? App.Settings.BaseUrl,
            localToken,
            result.UserID ?? "sidecar",
            null);

        await WaitForSyncAsync();
        HideProgress();
        ShowVerifyDeviceStep();
    }

    private async Task WaitForSyncAsync()
    {
        ShowProgress("Syncing your chats...");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        int lastRooms = 0;
        DateTime lastChange = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            var status = await App.Api.AuthStatusAsync();
            if (status == null) continue;

            if (!status.LoggedIn)
            {
                App.Settings.ClearSession();
                HideProgress();
                ShowEmailStep();
                return;
            }

            var rooms = status.RoomCount;
            var accounts = status.AccountCount;
            StatusBar.Message = $"Syncing... {rooms} chats, {accounts} networks loaded";

            if (rooms != lastRooms)
            {
                lastRooms = rooms;
                lastChange = DateTime.UtcNow;
            }

            if (rooms >= 5 && (accounts >= 2 || (DateTime.UtcNow - lastChange).TotalSeconds > 5))
                return;
        }
    }

    private void ShowVerifyDeviceStep()
    {
        EmailPanel.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Collapsed;
        RecoveryPanel.Visibility = Visibility.Visible;
        StatusBar.Message = "Synced! Verify this device to decrypt your message history.";
        StatusBar.IsOpen = true;
        ErrorBar.IsOpen = false;
    }

    private bool _verificationPolling;

    private async Task StartDeviceVerificationAsync()
    {
        ErrorBar.IsOpen = false;
        VerifyDeviceBtn.IsEnabled = false;
        ShowProgress("Starting device verification...");

        var result = await App.Api.StartDeviceVerificationAsync();
        if (result == null || result.Status != "started")
        {
            var errMsg = App.Api.LastError ?? "Failed to start verification";
            if (errMsg.Length > 200) errMsg = errMsg[..200] + "...";
            ErrorBar.Message = $"Verification failed: {errMsg}";
            ErrorBar.IsOpen = true;
            HideProgress();
            VerifyDeviceBtn.IsEnabled = true;
            return;
        }

        RecoveryPanel.Visibility = Visibility.Collapsed;
        VerificationPanel.Visibility = Visibility.Visible;
        VerifyStatusText.Text = "Open Beeper on another device and accept the verification request...";
        EmojiRepeater.Visibility = Visibility.Collapsed;
        ConfirmEmojiBtn.Visibility = Visibility.Collapsed;
        CancelVerifyBtn.Visibility = Visibility.Visible;
        StatusBar.Message = "Verification started. Check your other device.";
        StatusBar.IsOpen = true;
        HideProgress();

        _verificationPolling = true;
        _ = PollVerificationStatusAsync();
    }

    private async Task PollVerificationStatusAsync()
    {
        while (_verificationPolling)
        {
            await Task.Delay(1000);
            if (!_verificationPolling) break;

            var status = await App.Api.GetVerificationStatusAsync();
            if (status == null) continue;

            switch (status.Status)
            {
                case "emojis_ready":
                    ShowVerificationEmojis(status.Emojis);
                    return;

                case "done":
                    _verificationPolling = false;
                    StatusBar.Message = "Verification complete! Encryption keys will be shared automatically.";
                    StatusBar.IsOpen = true;
                    await Task.Delay(2000);
                    FinishAndShowShell();
                    return;

                case "cancelled":
                case "error":
                    _verificationPolling = false;
                    ErrorBar.Message = $"Verification failed: {status.Error ?? status.Status}";
                    ErrorBar.IsOpen = true;
                    CancelVerificationUI();
                    return;

                case "requested":
                    VerifyStatusText.Text = "Waiting for the other device to accept...";
                    break;
            }
        }
    }

    private void ShowVerificationEmojis(List<SASEmojiItem>? emojis)
    {
        if (emojis == null || emojis.Count == 0) return;

        VerifyStatusText.Text = "Verify that the following emojis match on both devices:";
        EmojiRepeater.ItemsSource = emojis;
        EmojiRepeater.Visibility = Visibility.Visible;
        ConfirmEmojiBtn.Visibility = Visibility.Visible;
        CancelVerifyBtn.Visibility = Visibility.Visible;
        StatusBar.Message = "Compare emojis with your other device";
        StatusBar.IsOpen = true;
    }

    private async Task ConfirmVerificationAsync()
    {
        ConfirmEmojiBtn.IsEnabled = false;
        ConfirmEmojiBtn.Visibility = Visibility.Collapsed;
        ShowProgress("Confirming verification...");

        var result = await App.Api.ConfirmVerificationAsync();
        if (result == null || result.Status != "confirmed")
        {
            var errMsg = App.Api.LastError ?? "Confirmation failed";
            if (errMsg.Length > 200) errMsg = errMsg[..200] + "...";
            ErrorBar.Message = $"Confirmation failed: {errMsg}";
            ErrorBar.IsOpen = true;
            HideProgress();
            ConfirmEmojiBtn.IsEnabled = true;
            ConfirmEmojiBtn.Visibility = Visibility.Visible;
            return;
        }

        VerifyStatusText.Text = "Waiting for the other device to confirm...";
        HideProgress();

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(1000);
            var status = await App.Api.GetVerificationStatusAsync();
            if (status?.Status == "done")
            {
                StatusBar.Message = "Verification complete! Your device is now trusted.";
                StatusBar.IsOpen = true;
                VerifyStatusText.Text = "Verification successful!";
                await Task.Delay(2000);
                FinishAndShowShell();
                return;
            }
            if (status?.Status is "cancelled" or "error")
            {
                ErrorBar.Message = $"Verification failed: {status.Error ?? status.Status}";
                ErrorBar.IsOpen = true;
                CancelVerificationUI();
                return;
            }
        }

        StatusBar.Message = "Verification may have completed. Proceeding...";
        StatusBar.IsOpen = true;
        await Task.Delay(1500);
        FinishAndShowShell();
    }

    private void CancelVerificationUI()
    {
        _verificationPolling = false;
        VerificationPanel.Visibility = Visibility.Collapsed;
        RecoveryPanel.Visibility = Visibility.Visible;
        VerifyDeviceBtn.IsEnabled = true;
        HideProgress();
    }

    private void FinishAndShowShell()
    {
        _verificationPolling = false;
        HideProgress();
        ((App)Application.Current).ShowShell();
    }

    private async Task AutoConnectAsync(string token)
    {
        BeeperApiService.SetBaseUrl(App.Settings.BaseUrl);
        App.Api.SetToken(token);

        ShowProgress("Starting...");
        if (!App.IsSidecarRunning)
        {
            App.StartSidecar(App.Settings.SidecarPort);
            await Task.Delay(2000);
        }

        var status = await App.Api.AuthStatusAsync();
        if (status == null || !status.LoggedIn)
        {
            App.Settings.ClearSession();
            App.Api.SetToken("");
            HideProgress();
            ShowEmailStep();
            return;
        }

        await WaitForSyncAsync();
        ((App)Application.Current).ShowShell();
    }

    private void ShowProgress(string message)
    {
        ConnectProgress.IsActive = true;
        ConnectProgress.Visibility = Visibility.Visible;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void HideProgress()
    {
        ConnectProgress.IsActive = false;
        ConnectProgress.Visibility = Visibility.Collapsed;
    }
}
