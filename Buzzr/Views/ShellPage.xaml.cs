using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Buzzr.Services;
using static Buzzr.Theme.T;

namespace Buzzr.Views;

public sealed partial class ShellPage : Page
{
    private List<BeeperAccount> _accounts = [];
    private string? _selectedAccountId;
    private string? _selectedSpaceId;

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
            _selectedSpaceId = null;
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

    private string? _draggedAccountId;
    private DateTime _lastNetSwap;

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

        var hidden = GetHiddenNetworks();

        foreach (var account in _accounts)
        {
            if (hidden.Contains(account.AccountId)) continue;
            var networkName = NetName(account.AccountId, account.Network);
            var iconContainer = new Grid { Width = 44, Height = 40 };
            iconContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            iconContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            iconContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });

            var selIndicator = new Border
            {
                Width = 3, Height = 18,
                CornerRadius = new CornerRadius(0, 2, 2, 0),
                Background = B(Accent),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Tag = "sel"
            };
            Grid.SetColumn(selIndicator, 0);
            iconContainer.Children.Add(selIndicator);

            var iconBorder = NetIcon(account.AccountId, 32, account.Network);
            Grid.SetColumn(iconBorder, 1);
            iconContainer.Children.Add(iconBorder);

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
            iconContainer.Tag = capturedId;

            iconContainer.CanDrag = true;
            iconContainer.AllowDrop = true;

            iconContainer.DragStarting += (s, e) =>
            {
                _draggedAccountId = capturedId;
                e.Data.RequestedOperation = DataPackageOperation.Move;
                ((Grid)s).Opacity = 0.4;
            };

            iconContainer.DropCompleted += (s, e) =>
            {
                ((Grid)s).Opacity = 1;
                _draggedAccountId = null;
            };

            iconContainer.DragOver += (s, e) =>
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                if (_draggedAccountId == null || _draggedAccountId == capturedId) return;
                if ((DateTime.UtcNow - _lastNetSwap).TotalMilliseconds < 350) return;
                var fromIdx = _accounts.FindIndex(a => a.AccountId == _draggedAccountId);
                var toIdx = _accounts.FindIndex(a => a.AccountId == capturedId);
                if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
                {
                    _lastNetSwap = DateTime.UtcNow;
                    var moved = _accounts[fromIdx];
                    _accounts.RemoveAt(fromIdx);
                    _accounts.Insert(toIdx, moved);
                    var child = AccountIconsStack.Children[fromIdx];
                    AccountIconsStack.Children.RemoveAt(fromIdx);
                    DispatcherQueue.TryEnqueue(() => AccountIconsStack.Children.Insert(toIdx, child));
                }
            };

            iconContainer.Drop += (s, e) =>
            {
                App.Settings.SetString("network_order", string.Join(",", _accounts.Select(a => a.AccountId)));
                _draggedAccountId = null;
            };

            iconContainer.PointerPressed += (s, _) =>
            {
                if (_selectedAccountId == capturedId && _selectedSpaceId == null)
                {
                    _selectedAccountId = null;
                    _selectedSpaceId = null;
                    HighlightSelectedAccount();
                    ChatList.FilterByAccount(null);
                }
                else
                {
                    _selectedAccountId = capturedId;
                    _selectedSpaceId = null;
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

            AddNetworkContextMenu(iconContainer, capturedId, networkName);
            AccountIconsStack.Children.Add(iconContainer);

            var net = ResolveNetwork(account.AccountId, account.Network);
            if (net == "discord")
            {
                var spaceIds = ChatList.GetDistinctSpaceIds(account.AccountId);
                foreach (var spaceId in spaceIds)
                {
                    var spaceKey = $"space:{account.AccountId}:{spaceId}";
                    if (hidden.Contains(spaceKey)) continue;

                    var spaceName = ChatList.GetSpaceName(account.AccountId, spaceId) ?? "Server";
                    var spaceAvatar = ChatList.GetSpaceAvatar(account.AccountId, spaceId);

                    var spaceContainer = new Grid { Width = 44, Height = 40, Tag = $"{account.AccountId}:{spaceId}" };
                    spaceContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                    spaceContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    spaceContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });

                    var spaceSel = new Border
                    {
                        Width = 3, Height = 18,
                        CornerRadius = new CornerRadius(0, 2, 2, 0),
                        Background = B(Accent),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        Visibility = Visibility.Collapsed,
                        Tag = "space_sel"
                    };
                    Grid.SetColumn(spaceSel, 0);
                    spaceContainer.Children.Add(spaceSel);

                    var spaceContent = new Grid();
                    var initial = spaceName.Length > 0 ? spaceName[0].ToString().ToUpper() : "?";
                    spaceContent.Children.Add(new TextBlock
                    {
                        Text = initial,
                        FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = B(Microsoft.UI.Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                    if (!string.IsNullOrEmpty(spaceAvatar))
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 64, DecodePixelHeight = 64 };
                        try { bmp.UriSource = new Uri(spaceAvatar); } catch { }
                        var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Opacity = 0 };
                        img.ImageOpened += (s, _) =>
                        {
                            ((Image)s).Opacity = 1;
                            if (((Image)s).Parent is Grid g && g.Children[0] is TextBlock tb)
                                tb.Visibility = Visibility.Collapsed;
                        };
                        img.ImageFailed += (s, _) => ((Image)s).Visibility = Visibility.Collapsed;
                        spaceContent.Children.Add(img);
                    }

                    var spaceBorder = new Border
                    {
                        Width = 32, Height = 32,
                        CornerRadius = new CornerRadius(16),
                        Background = B(Windows.UI.Color.FromArgb(255, 88, 101, 242)),
                        Child = spaceContent,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    spaceBorder.Loaded += (s, _) =>
                    {
                        var b = (Border)s;
                        b.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 32, 32) };
                    };
                    Grid.SetColumn(spaceBorder, 1);
                    spaceContainer.Children.Add(spaceBorder);

                    ToolTipService.SetToolTip(spaceContainer, spaceName);

                    var capAcct = account.AccountId;
                    var capSpace = spaceId;
                    spaceContainer.PointerPressed += (s, _) =>
                    {
                        _selectedAccountId = capAcct;
                        _selectedSpaceId = capSpace;
                        HighlightSelectedAccount();
                        ChatList.FilterByAccount(capAcct, capSpace);
                    };
                    spaceContainer.PointerEntered += (s, _) => spaceBorder.Opacity = 0.8;
                    spaceContainer.PointerExited += (s, _) => spaceBorder.Opacity = 1.0;

                    AddNetworkContextMenu(spaceContainer, spaceKey, spaceName, true);
                    AccountIconsStack.Children.Add(spaceContainer);
                }
            }
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
        foreach (var child in AccountIconsStack.Children)
        {
            if (child is not Grid container) continue;
            foreach (var sub in container.Children)
            {
                if (sub is Border b && b.Tag is string tag)
                {
                    if (tag == "sel")
                    {
                        var acctId = container.Tag as string;
                        AnimateIndicator(b, acctId == _selectedAccountId && _selectedSpaceId == null);
                    }
                    else if (tag == "space_sel")
                    {
                        var spaceKey = container.Tag as string;
                        AnimateIndicator(b, spaceKey == $"{_selectedAccountId}:{_selectedSpaceId}");
                    }
                }
            }
        }

        var allIcon = AllChatsBtn.Content as FontIcon;
        if (allIcon != null)
            allIcon.Foreground = B(_selectedAccountId == null ? Accent : Fg3);
    }

    private HashSet<string> GetHiddenNetworks()
    {
        var hidden = App.Settings.GetString("hidden_networks");
        return string.IsNullOrEmpty(hidden) ? [] : hidden.Split(',').ToHashSet();
    }

    private void SetHiddenNetworks(HashSet<string> hidden)
    {
        App.Settings.SetString("hidden_networks", hidden.Count > 0 ? string.Join(",", hidden) : null);
    }

    private void AddNetworkContextMenu(FrameworkElement element, string id, string displayName, bool isSpace = false)
    {
        var flyout = new MenuFlyout();
        var hidden = GetHiddenNetworks();

        var hideItem = new MenuFlyoutItem
        {
            Text = $"Hide {displayName}",
            Icon = new FontIcon { Glyph = "\uED1A" }
        };
        hideItem.Click += (s, e) =>
        {
            var h = GetHiddenNetworks();
            h.Add(id);
            SetHiddenNetworks(h);
            RenderAccountIcons();
            UpdateDiscordSubIcons();
        };
        flyout.Items.Add(hideItem);

        if (hidden.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var showAllItem = new MenuFlyoutItem
            {
                Text = "Show all networks",
                Icon = new FontIcon { Glyph = "\uE7B3" }
            };
            showAllItem.Click += (s, e) =>
            {
                SetHiddenNetworks([]);
                RenderAccountIcons();
                UpdateDiscordSubIcons();
            };
            flyout.Items.Add(showAllItem);
        }

        element.ContextFlyout = flyout;
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
        }

        _ = PreloadAndRenderAsync();
    }

    private async Task PreloadAndRenderAsync()
    {
        foreach (var account in _accounts)
        {
            var net = ResolveNetwork(account.AccountId, account.Network);
            if (net == "discord")
                await ChatList.PreloadSpaceRoomsAsync(account.AccountId);
        }
        RenderAccountIcons();
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
