using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Win32;
using Buzzr.Services;
using static Buzzr.Theme.T;

namespace Buzzr.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly Dictionary<string, string[]> _panelKeywords = new()
    {
        { "preferences", new[] { "startup", "open on startup", "launch", "windows starts", "theme" } },
        { "storage", new[] { "cache", "avatar cache", "database", "clear", "size", "mb" } },
        { "about", new[] { "version", "buzzr", "winui", "highest.dev", "beeper", "client" } },
    };

    public SettingsPage()
    {
        this.InitializeComponent();

        SignOutBtn.Click += (s, e) => ((App)Application.Current).Disconnect();
        ClearCacheBtn.Click += (s, e) => { ChatListControl.ClearAllCaches(); RefreshStorageStats(); };
        StartupToggle.Toggled += (s, e) => SetStartupEnabled(StartupToggle.IsOn);
        NotificationsToggle.Toggled += (s, e) => App.Settings.NotificationsEnabled = NotificationsToggle.IsOn;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ShowPanel("profile");
        VersionText.Text = $"v{App.Version}";
        LoadProfile();
        RefreshStorageStats();
        StartupToggle.IsOn = App.Settings.GetString("open_on_startup") == "true";
        NotificationsToggle.IsOn = App.Settings.NotificationsEnabled;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        App.RootFrame?.Navigate(typeof(ShellPage));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
            ShowPanel(item.Tag?.ToString() ?? "");
    }

    private void ProfileCardBtn_Click(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = null;
        ShowPanel("profile");
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var query = sender.Text;
        FilterNavItems(query);

        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem nvi && nvi.Visibility == Visibility.Visible)
                {
                    NavView.SelectedItem = nvi;
                    return;
                }
            }
        }
    }

    private void ShowPanel(string tag)
    {
        ProfilePanel.Visibility = tag == "profile" ? Visibility.Visible : Visibility.Collapsed;
        PreferencesPanel.Visibility = tag == "preferences" ? Visibility.Visible : Visibility.Collapsed;
        StoragePanel.Visibility = tag == "storage" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        HeaderText.Text = tag switch
        {
            "profile" => "Profile",
            "preferences" => "Preferences",
            "storage" => "Storage",
            "about" => "About",
            _ => ""
        };

        ProfileCardBtn.Background = tag == "profile"
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private async void LoadProfile()
    {
        var userId = App.Settings.UserId ?? "";
        string username;
        if (userId.StartsWith("@") && userId.Contains(":"))
            username = userId[1..userId.IndexOf(':')];
        else if (!string.IsNullOrEmpty(userId))
            username = userId;
        else
            username = "Unknown";

        UsernameText.Text = username;
        SidebarUsername.Text = username;
        AvatarInitial.Text = string.IsNullOrEmpty(username) ? "?" : username[0].ToString().ToUpper();
        SidebarAvatarInitial.Text = AvatarInitial.Text;

        EmailText.Text = App.Settings.BeeperEmail ?? "Not set";
        SidebarEmail.Text = EmailText.Text;
        UserIdText.Text = string.IsNullOrEmpty(userId) ? "Not available" : userId;

        bool connected = false;
        try { var info = await App.Api.GetInfoAsync(); connected = info != null; } catch { }
        ConnectionText.Text = connected ? "Connected" : "Disconnected";
        ConnectionDot.Fill = B(connected ? Ok : Err);

        try
        {
            var accounts = await App.Api.GetAccountsAsync();
            var beeper = accounts.FirstOrDefault(a => a.AccountId == "hungryserv");
            if (beeper?.User?.ImgUrl != null)
            {
                var imgUrl = beeper.User.ImgUrl;
                if (imgUrl.StartsWith("mxc://"))
                    imgUrl = BeeperApiService.GetAssetUrl(imgUrl);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(imgUrl));
                AvatarImage.Source = bmp;
                AvatarImageBorder.Visibility = Visibility.Visible;
                AvatarFallback.Visibility = Visibility.Collapsed;
                SidebarAvatarImage.Source = bmp;
                SidebarAvatarImageBorder.Visibility = Visibility.Visible;
                SidebarAvatarFallback.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
    }

    private void RefreshStorageStats()
    {
        try
        {
            var (count, bytes) = ChatListControl.GetAvatarCacheStats();
            AvatarCacheText.Text = $"{count} files, {bytes / 1024.0 / 1024.0:F1} MB";
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buzzr", "store.db");
            long dbSize = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
            DbSizeText.Text = $"{dbSize / 1024.0 / 1024.0:F1} MB";
            TotalSizeText.Text = $"{(bytes + dbSize) / 1024.0 / 1024.0:F1} MB";
        }
        catch { }
    }

    private void SetStartupEnabled(bool enable)
    {
        App.Settings.SetString("open_on_startup", enable ? "true" : "false");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
                key.SetValue("Buzzr", $"\"{Path.Combine(AppContext.BaseDirectory, "Buzzr.exe")}\"");
            else
                key.DeleteValue("Buzzr", false);
        }
        catch { }
    }

    private void FilterNavItems(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var item in NavView.MenuItems)
                if (item is NavigationViewItem nvi) nvi.Visibility = Visibility.Visible;
            return;
        }
        query = query.ToLowerInvariant();
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nvi)
            {
                var content = nvi.Content?.ToString()?.ToLowerInvariant() ?? "";
                var tag = nvi.Tag?.ToString()?.ToLowerInvariant() ?? "";
                bool match = content.Contains(query) || tag.Contains(query);
                if (!match && _panelKeywords.TryGetValue(tag, out var keywords))
                    match = keywords.Any(k => k.Contains(query));
                nvi.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
