using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BeeperWinUI.Views;

public sealed partial class SetupPage : Page
{
    public SetupPage()
    {
        this.InitializeComponent();
        ConnectBtn.Click += (s, e) => _ = ConnectAsync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Restore saved token and auto-connect
        var saved = App.Settings.AccessToken;
        if (!string.IsNullOrEmpty(saved))
        {
            TokenBox.Password = saved;
            _ = AutoConnectAsync(saved);
        }
    }

    private async Task ConnectAsync()
    {
        var token = TokenBox.Password.Trim();
        if (string.IsNullOrEmpty(token))
        {
            ErrorBar.Message = "Please enter your API token.";
            ErrorBar.IsOpen = true;
            return;
        }

        ErrorBar.IsOpen = false;
        ConnectBtn.IsEnabled = false;
        ConnectProgress.IsActive = true;
        ConnectProgress.Visibility = Visibility.Visible;
        StatusBar.Message = "Connecting to Beeper Desktop...";
        StatusBar.IsOpen = true;

        App.Api.SetToken(token);
        var info = await App.Api.GetInfoAsync();

        if (info != null)
        {
            App.Settings.SaveSession("localhost", token, "local", null);
            ((App)Application.Current).ShowShell();
        }
        else
        {
            ErrorBar.Message = $"Could not connect: {App.Api.LastError ?? "unknown"}\nMake sure Beeper Desktop is running with the API enabled.";
            ErrorBar.IsOpen = true;
            StatusBar.IsOpen = false;
            ConnectBtn.IsEnabled = true;
            ConnectProgress.IsActive = false;
            ConnectProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task AutoConnectAsync(string token)
    {
        App.Api.SetToken(token);
        ConnectBtn.IsEnabled = false;
        ConnectProgress.IsActive = true;
        ConnectProgress.Visibility = Visibility.Visible;
        StatusBar.Message = "Reconnecting...";
        StatusBar.IsOpen = true;

        var info = await App.Api.GetInfoAsync();

        if (info != null)
        {
            ((App)Application.Current).ShowShell();
        }
        else
        {
            StatusBar.IsOpen = false;
            ConnectProgress.IsActive = false;
            ConnectProgress.Visibility = Visibility.Collapsed;
            ConnectBtn.IsEnabled = true;
        }
    }
}
