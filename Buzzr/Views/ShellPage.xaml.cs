using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Buzzr.Services;
using static Buzzr.Theme.T;

namespace Buzzr.Views;

public sealed partial class ShellPage : Page
{
    private List<BeeperAccount> _accounts = [];
    private string? _selectedAccountId;

    public static Action? FocusSearch;
    public static Action? OpenNewChat;
    public static Action? CloseOverlays;
    public static Action? OpenTerminal;

    public ShellPage()
    {
        this.InitializeComponent();

        ChatList.ChatSelected += (chat) => _ = MessagePanel.LoadChat(chat);
        ChatList.MessageReceived += (chatId, msgJson) => MessagePanel.OnNewMessage(chatId, msgJson);
        ChatList.ChatsLoaded += UpdateDiscordSubIcons;

        AllChatsBtn.Click += (s, e) =>
        {
            _selectedAccountId = null;
            HighlightSelectedAccount();
            ChatList.FilterByAccount(null);
        };

        SettingsItem.Click += (s, e) => App.RootFrame?.Navigate(typeof(SettingsPage));
        DisconnectItem.Click += (s, e) => ((App)Application.Current).Disconnect();
        AboutItem.Click += (s, e) => _ = ShowAboutAsync();

        FocusSearch = () => ChatList.FocusSearchBox();
        OpenNewChat = () => _ = ShowNewChatDialogAsync();
        CloseOverlays = () => MessagePanel.CloseOverlays();
        OpenTerminal = () => _ = ShowTerminalAsync();
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
        OpenTerminal = null;
    }

    private async Task LoadAccountsAsync()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(2000);
            try
            {
                var accounts = await App.Api.GetAccountsAsync();
                if (accounts.Count > 0 && accounts.Count >= _accounts.Count)
                {
                    _accounts = accounts;
                    AppLog.Write($"[Accounts] Loaded {accounts.Count} accounts from API (attempt {attempt})");
                    ChatList.SetAccounts(_accounts);
                    RenderAccountIcons();
                    UpdateSettingsFlyout();
                    return;
                }
            }
            catch { }
        }
    }

    private static readonly string[] DefaultNetworkOrder = [
        "hungryserv", "whatsapp", "discord", "discordgo", "facebook", "facebookgo",
        "messenger", "instagram", "instagramgo", "telegram", "telegramgo",
        "signal", "signalgo", "gmessages", "linkedin", "slack", "slackgo",
        "twitter", "twittergo", "googlechat", "line", "sh-line", "imessage", "imessagego"
    ];

    private void RenderAccountIcons()
    {
        AccountIconsStack.Children.Clear();

        var savedOrder = App.Settings.GetString("network_order");
        var orderList = !string.IsNullOrEmpty(savedOrder)
            ? savedOrder.Split(',').ToList()
            : DefaultNetworkOrder.ToList();

        _accounts = _accounts.OrderBy(a =>
        {
            var idx = orderList.IndexOf(a.AccountId);
            return idx >= 0 ? idx : 999;
        }).ToList();

        foreach (var account in _accounts)
        {
            var networkName = NetName(account.AccountId, account.Network);
            var iconContainer = new Grid { Width = 36, Height = 36 };

            var iconBorder = NetIcon(account.AccountId, 32, account.Network);
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

    private void UpdateDiscordSubIcons()
    {
        var chatAccountIds = ChatList.GetAllChatAccountIds();
        var knownIds = new HashSet<string>(_accounts.Select(a => a.AccountId));
        int added = 0;
        foreach (var aid in chatAccountIds)
        {
            if (!knownIds.Contains(aid))
            {
                if (aid is "slackgo" or "imessagego" or "telegramgo" or "signalgo" or "instagramgo" or "twittergo")
                {
                    AppLog.Write($"[Accounts] Skipping synthetic account for {aid} (not confirmed by API)");
                    continue;
                }
                AppLog.Write($"[Accounts] Missing from API, found in chats: {aid}");
                var synthetic = new BeeperAccount { AccountId = aid };
                _accounts.Add(synthetic);
                knownIds.Add(aid);
                added++;
            }
        }

        if (added > 0)
        {
            AppLog.Write($"[Accounts] Added {added} synthetic account(s), re-rendering icons");
            ChatList.SetAccounts(_accounts);
            RenderAccountIcons();
        }

        foreach (var account in _accounts)
        {
            var net = ResolveNetwork(account.AccountId, account.Network);
            if (net == "discord")
            {
                var spaceIds = ChatList.GetDistinctSpaceIds(account.AccountId);
                AppLog.Write($"[Discord] Account {account.AccountId}: {spaceIds.Count} space IDs found");
                foreach (var sid in spaceIds.Take(5))
                    AppLog.Write($"  SpaceId={sid}, Name={ChatList.GetSpaceName(account.AccountId, sid)}");
            }
        }

        int insertOffset = 0;
        for (int i = 0; i < _accounts.Count; i++)
        {
            var account = _accounts[i];
            var net = ResolveNetwork(account.AccountId, account.Network);
            if (net != "discord") continue;

            var spaceIds = ChatList.GetDistinctSpaceIds(account.AccountId);
            if (spaceIds.Count <= 1) continue;

            int insertPos = i + 1 + insertOffset;
            int serverNum = 0;
            foreach (var spaceId in spaceIds)
            {
                serverNum++;
                var spaceName = ChatList.GetSpaceName(account.AccountId, spaceId) ?? $"Server {serverNum}";

                var subIcon = new Grid { Width = 36, Height = 36 };
                var dot = NetDot(account.AccountId, 24, account.Network);
                dot.HorizontalAlignment = HorizontalAlignment.Center;
                dot.VerticalAlignment = VerticalAlignment.Center;
                dot.Child = new TextBlock
                {
                    Text = spaceName.Length > 0 ? spaceName[0].ToString().ToUpper() : "?",
                    FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = B(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                subIcon.Children.Add(dot);
                ToolTipService.SetToolTip(subIcon, spaceName);

                var capturedAccountId = account.AccountId;
                var capturedSpaceId = spaceId;
                subIcon.PointerPressed += (s, _) =>
                {
                    _selectedAccountId = capturedAccountId;
                    HighlightSelectedAccount();
                    ChatList.FilterByAccount(capturedAccountId, capturedSpaceId);
                };
                subIcon.PointerEntered += (s, _) => dot.Opacity = 0.8;
                subIcon.PointerExited += (s, _) => dot.Opacity = 1.0;

                if (insertPos <= AccountIconsStack.Children.Count)
                    AccountIconsStack.Children.Insert(insertPos, subIcon);
                else
                    AccountIconsStack.Children.Add(subIcon);
                insertPos++;
                insertOffset++;
            }
        }
    }

    private void UpdateSettingsFlyout()
    {
        string displayName = "User";

        var beeperAcct = _accounts.FirstOrDefault(a => a.AccountId == "hungryserv");
        if (beeperAcct?.User != null)
        {
            var name = beeperAcct.User.FullName ?? beeperAcct.User.Username ?? beeperAcct.User.DisplayText;
            if (!string.IsNullOrEmpty(name) && name != "Beeper" && name != beeperAcct.Network)
                displayName = name;
        }

        if (displayName == "User")
        {
            var userId = App.Settings.UserId;
            if (!string.IsNullOrEmpty(userId) && userId.StartsWith("@") && userId.Contains(":"))
                displayName = userId[1..userId.IndexOf(':')];
        }

        UserNameItem.Text = displayName;
        AccountCountItem.Text = $"{_accounts.Count} connected account{(_accounts.Count != 1 ? "s" : "")}";
    }

    private async Task ShowAboutAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "About Buzzr",
            Content = "Buzzr\nUnofficial Windows client for Beeper\n\nBuilt with WinUI 3",
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

    private async Task ShowTerminalAsync()
    {
        var outputBlock = new TextBlock
        {
            Text = "Buzzr Terminal v1.0\nType 'help' for available commands.\n\n> ",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code,Consolas,Courier New"),
            FontSize = 13,
            Foreground = B(Microsoft.UI.Colors.LimeGreen),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var inputBox = new TextBox
        {
            PlaceholderText = "Enter command...",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code,Consolas,Courier New"),
            FontSize = 13,
            Background = B(Windows.UI.Color.FromArgb(255, 10, 10, 10)),
            Foreground = B(Microsoft.UI.Colors.LimeGreen),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = B(Windows.UI.Color.FromArgb(100, 0, 255, 0)),
            Padding = new Thickness(8, 6, 8, 6),
        };

        var scroll = new ScrollViewer
        {
            Content = outputBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 320,
        };

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(new Border
        {
            Child = scroll,
            Background = B(Windows.UI.Color.FromArgb(255, 10, 10, 10)),
            Padding = new Thickness(12, 10, 12, 6),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
        });
        panel.Children.Add(inputBox);

        var dialog = new ContentDialog
        {
            Title = "\uE756  Terminal",
            Content = panel,
            CloseButtonText = "Close",
            XamlRoot = this.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            MinWidth = 500,
        };

        dialog.Resources["ContentDialogForeground"] = B(Microsoft.UI.Colors.LimeGreen);

        inputBox.KeyDown += (s, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            var cmd = inputBox.Text.Trim().ToLowerInvariant();
            inputBox.Text = "";
            var response = ExecuteTerminalCommand(cmd);
            outputBlock.Text += cmd + "\n" + response + "\n> ";
            scroll.ChangeView(null, scroll.ScrollableHeight + 200, null);
        };

        await dialog.ShowAsync();
    }

    private string ExecuteTerminalCommand(string cmd)
    {
        switch (cmd)
        {
            case "help":
                return "Available commands:\n" +
                       "  censor    - Toggle censor mode (hide real names/messages)\n" +
                       "  uncensor  - Disable censor mode\n" +
                       "  status    - Show app status\n" +
                       "  accounts  - List connected accounts\n" +
                       "  clear     - (just close and reopen)\n" +
                       "  help      - Show this message";

            case "censor":
                ChatList.SetCensorMode(true);
                return "[OK] Censor mode enabled. All names and previews are now hidden.";

            case "uncensor":
                ChatList.SetCensorMode(false);
                return "[OK] Censor mode disabled. Real data restored.";

            case "status":
                var connected = App.Api.IsConnected ? "Connected" : "Disconnected";
                var censored = ChatList.IsCensored ? "ON" : "OFF";
                return $"  Connection: {connected}\n  Accounts: {_accounts.Count}\n  Censor mode: {censored}";

            case "accounts":
                if (_accounts.Count == 0) return "  No accounts loaded.";
                var sb = new System.Text.StringBuilder();
                foreach (var a in _accounts)
                    sb.AppendLine($"  [{ResolveNetwork(a.AccountId, a.Network)}] {a.AccountId}");
                return sb.ToString().TrimEnd();

            default:
                if (string.IsNullOrEmpty(cmd)) return "";
                return $"Unknown command: '{cmd}'. Type 'help' for available commands.";
        }
    }
}
