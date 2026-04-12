using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BeeperWinUI.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        this.InitializeComponent();

        BackBtn.Click += (s, e) =>
        {
            if (App.RootFrame?.CanGoBack == true)
                App.RootFrame.GoBack();
            else
                App.RootFrame?.Navigate(typeof(ShellPage));
        };

        ClearCacheBtn.Click += (s, e) =>
        {
            ChatListControl.ClearAllCaches();
            RefreshCacheStats();
        };

        SignOutBtn.Click += (s, e) =>
        {
            ((App)Application.Current).Disconnect();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshCacheStats();
        LoadAccountInfo();
    }

    private void RefreshCacheStats()
    {
        var (avatarCount, avatarBytes) = ChatListControl.GetAvatarCacheStats();
        var avatarMB = avatarBytes / (1024.0 * 1024.0);
        AvatarCacheInfo.Text = avatarCount == 0
            ? "Empty"
            : $"{avatarCount} files, {avatarMB:F1} MB";

        var chatBytes = ChatListControl.GetChatCacheSize();
        var chatMB = chatBytes / (1024.0 * 1024.0);
        ChatCacheInfo.Text = chatBytes == 0
            ? "Empty"
            : $"{chatMB:F2} MB";
    }

    private void LoadAccountInfo()
    {
        var userId = App.Settings.UserId;
        var email = App.Settings.BeeperEmail;
        if (!string.IsNullOrEmpty(email))
            UsernameText.Text = email;
        else if (!string.IsNullOrEmpty(userId))
            UsernameText.Text = userId;
        else
            UsernameText.Text = "Unknown";

        var mode = App.Settings.ConnectionMode;
        ConnectionModeText.Text = mode == "sidecar"
            ? $"Standalone (sidecar, port {App.Settings.SidecarPort})"
            : "Beeper Desktop (legacy)";
    }
}
