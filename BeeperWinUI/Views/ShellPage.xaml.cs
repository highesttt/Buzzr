using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using BeeperWinUI.Services;
using static BeeperWinUI.Theme.T;

namespace BeeperWinUI.Views;

public sealed partial class ShellPage : Page
{
    private List<BeeperAccount> _accounts = [];
    private string? _selectedAccountId;

    public static Action? FocusSearch;
    public static Action? OpenNewChat;
    public static Action? CloseOverlays;

    public ShellPage()
    {
        this.InitializeComponent();

        ChatList.ChatSelected += (chat) => _ = MessagePanel.LoadChat(chat);
        ChatList.MessageReceived += (chatId, msgJson) => MessagePanel.OnNewMessage(chatId, msgJson);

        AllChatsBtn.Click += (s, e) =>
        {
            _selectedAccountId = null;
            HighlightSelectedAccount();
            ChatList.FilterByAccount(null);
        };

        DisconnectItem.Click += (s, e) => ((App)Application.Current).Disconnect();
        AboutItem.Click += (s, e) => _ = ShowAboutAsync();

        FocusSearch = () => ChatList.FocusSearchBox();
        OpenNewChat = () => _ = ShowNewChatDialogAsync();
        CloseOverlays = () => MessagePanel.CloseOverlays();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadAccountsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        FocusSearch = null;
        OpenNewChat = null;
        CloseOverlays = null;
    }

    private async Task LoadAccountsAsync()
    {
        try
        {
            var tokenStr = App.Settings.AccessToken ?? "";
            _accounts = await Task.Run(() => {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenStr);
                var body = http.GetStringAsync("http://localhost:23373/v1/accounts").GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<List<BeeperAccount>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            });
            ChatList.SetAccounts(_accounts);
            RenderAccountIcons();
            UpdateSettingsFlyout();
        }
        catch { }
    }

    private void RenderAccountIcons()
    {
        AccountIconsStack.Children.Clear();
        foreach (var account in _accounts)
        {
            var netColor = NetColor(account.AccountId);
            var networkName = account.Network ?? NetName(account.AccountId);
            var iconContainer = new Grid { Width = 36, Height = 36 };

            var iconBorder = new Border
            {
                Width = 32, Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = B(netColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = NetLogoElement(account.AccountId, 14, Black)
            };
            iconContainer.Children.Add(iconBorder);

            var selIndicator = new Border
            {
                Width = 3, Height = 20,
                CornerRadius = new CornerRadius(2),
                Background = B(Accent),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Tag = "sel"
            };
            iconContainer.Children.Add(selIndicator);

            var tooltipText = networkName;
            if (account.User != null)
            {
                var displayName = account.User.FullName ?? account.User.DisplayText
                    ?? account.User.Username ?? account.User.Email;
                if (!string.IsNullOrEmpty(displayName))
                    tooltipText += $"\n{displayName}";
            }
            ToolTipService.SetToolTip(iconContainer, tooltipText);

            var capturedId = account.AccountId;
            iconContainer.PointerPressed += (s, _) =>
            {
                if (_selectedAccountId == capturedId)
                {
                    _selectedAccountId = null;
                    HighlightSelectedAccount();
                    ChatList.FilterByAccount(null);
                }
                else
                {
                    _selectedAccountId = capturedId;
                    HighlightSelectedAccount();
                    ChatList.FilterByAccount(capturedId);
                }
            };

            iconContainer.PointerEntered += (s, _) =>
            {
                if (capturedId != _selectedAccountId)
                    iconBorder.Opacity = 0.8;
            };
            iconContainer.PointerExited += (s, _) =>
            {
                iconBorder.Opacity = 1.0;
            };

            AccountIconsStack.Children.Add(iconContainer);
        }
    }

    private static void AnimateIndicator(Border indicator, bool show)
    {
        indicator.Visibility = Visibility.Visible;
        var visual = ElementCompositionPreview.GetElementVisual(indicator);
        var compositor = visual.Compositor;
        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(1f, show ? 1f : 0f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(200);
        var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(1f, show ? Vector3.One : new Vector3(1f, 0f, 1f));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(200);
        visual.StartAnimation("Opacity", opacityAnim);
        visual.StartAnimation("Scale", scaleAnim);
        visual.CenterPoint = new Vector3(1.5f, 10f, 0f);
    }

    private void HighlightSelectedAccount()
    {
        int index = 0;
        foreach (var child in AccountIconsStack.Children)
        {
            if (child is Grid container && index < _accounts.Count)
            {
                var acctId = _accounts[index].AccountId;
                foreach (var sub in container.Children)
                {
                    if (sub is Border b && b.Tag as string == "sel")
                        AnimateIndicator(b, acctId == _selectedAccountId);
                }
                index++;
            }
        }

        var allIcon = AllChatsBtn.Content as FontIcon;
        if (allIcon != null)
            allIcon.Foreground = B(_selectedAccountId == null ? Accent : Fg3);
    }

    private void UpdateSettingsFlyout()
    {
        BeeperUser? selfUser = null;
        foreach (var a in _accounts)
        {
            if (a.User?.IsSelf == true) { selfUser = a.User; break; }
        }
        if (selfUser == null && _accounts.Count > 0)
            selfUser = _accounts[0].User;

        if (selfUser != null)
        {
            var name = selfUser.FullName ?? selfUser.DisplayText ?? selfUser.Username ?? "User";
            UserNameItem.Text = name;
        }
        else
        {
            UserNameItem.Text = "User";
        }

        AccountCountItem.Text = $"{_accounts.Count} connected account{(_accounts.Count != 1 ? "s" : "")}";
    }

    private async Task ShowAboutAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "About Beeper WinUI",
            Content = "Beeper WinUI\nA native Windows client for Beeper\n\nBuilt with WinUI 3",
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };
        await dialog.ShowAsync();
    }

    private async Task ShowNewChatDialogAsync()
    {
        var dialog = new NewChatDialog(_accounts);
        dialog.XamlRoot = this.XamlRoot;
        dialog.RequestedTheme = ElementTheme.Dark;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.SelectedChat != null)
        {
            _ = MessagePanel.LoadChat(dialog.SelectedChat);
        }
    }
}
