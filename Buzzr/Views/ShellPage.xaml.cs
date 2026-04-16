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
    private List<string> _sidebarOrder = [];
    private string? _selectedAccountId;
    private string? _selectedSpaceId;
    private Dictionary<string, SidebarFolder> _folders = [];
    private HashSet<string> _expandedFolders = [];

    private class SidebarFolder
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Folder";
        public string? Color { get; set; }
        public List<string> Items { get; set; } = [];
    }

    private static readonly string[] FolderColors = [
        "#3B3B3B", "#5865F2", "#57F287", "#FEE75C", "#ED4245",
        "#EB459E", "#F47B67", "#2D7D46", "#2D4F7D", "#7D2D6B"
    ];

    private static Windows.UI.Color ParseColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 7) return Windows.UI.Color.FromArgb(255, 35, 35, 35);
        byte r = Convert.ToByte(hex[1..3], 16);
        byte g = Convert.ToByte(hex[3..5], 16);
        byte b = Convert.ToByte(hex[5..7], 16);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private Windows.UI.Color GetFolderColor(SidebarFolder folder)
    {
        var c = ParseColor(folder.Color);
        byte pr = (byte)(c.R + (255 - c.R) * 0.3);
        byte pg = (byte)(c.G + (255 - c.G) * 0.3);
        byte pb = (byte)(c.B + (255 - c.B) * 0.3);
        return Windows.UI.Color.FromArgb(30, pr, pg, pb);
    }

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
        LoadFolders();

        var allItems = new List<string>();
        foreach (var account in _accounts)
        {
            allItems.Add(account.AccountId);
            var net = ResolveNetwork(account.AccountId, account.Network);
            if (net == "discord")
            {
                foreach (var spaceId in ChatList.GetDistinctSpaceIds(account.AccountId))
                    allItems.Add($"space:{account.AccountId}:{spaceId}");
            }
        }

        var folderItemIds = GetAllFolderItemIds();
        allItems.RemoveAll(id => folderItemIds.Contains(id));
        foreach (var folderId in _folders.Keys)
        {
            if (!allItems.Contains(folderId))
                allItems.Add(folderId);
        }

        var savedOrder = App.Settings.GetString("network_order");
        var orderList = !string.IsNullOrEmpty(savedOrder)
            ? savedOrder.Split(',').ToList()
            : DefaultNetworkOrder.ToList();

        allItems.Sort((a, b) =>
        {
            var ia = orderList.IndexOf(a);
            var ib = orderList.IndexOf(b);
            if (ia < 0) ia = 999;
            if (ib < 0) ib = 999;
            return ia.CompareTo(ib);
        });

        var hidden = GetHiddenNetworks();
        _sidebarOrder = allItems.Where(id => !hidden.Contains(id)).ToList();

        foreach (var f in _folders.Values)
            f.Items.RemoveAll(id => !folderItemIds.Contains(id) && !allItems.Contains(id));

        foreach (var itemId in _sidebarOrder)
        {
            if (itemId.StartsWith("folder:"))
                RenderFolderIcon(itemId);
            else if (itemId.StartsWith("space:"))
                RenderSpaceIcon(itemId);
            else
                RenderNetworkIcon(itemId);
        }

        SetupEmptySpaceContextMenu();
    }

    private void RenderNetworkIcon(string accountId)
    {
        var account = _accounts.FirstOrDefault(a => a.AccountId == accountId);
        if (account == null) return;

        var networkName = NetName(account.AccountId, account.Network);
        var iconContainer = MakeDraggableContainer(accountId);

        var selIndicator = MakeSelIndicator("sel");
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

        var capturedId = accountId;
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
            if (capturedId != _selectedAccountId) iconBorder.Opacity = 0.8;
        };
        iconContainer.PointerExited += (s, _) => iconBorder.Opacity = 1.0;

        AddNetworkContextMenu(iconContainer, capturedId, networkName);
        AccountIconsStack.Children.Add(iconContainer);
    }

    private void RenderSpaceIcon(string itemId)
    {
        var parts = itemId.Split(':', 3);
        if (parts.Length < 3) return;
        var accountId = parts[1];
        var spaceId = parts[2];

        var spaceName = ChatList.GetSpaceName(accountId, spaceId) ?? "Server";
        var spaceAvatar = ChatList.GetSpaceAvatar(accountId, spaceId);

        var spaceContainer = MakeDraggableContainer(itemId);
        spaceContainer.Tag = $"{accountId}:{spaceId}";

        var spaceSel = MakeSelIndicator("space_sel");
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

        var capAcct = accountId;
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

        AddNetworkContextMenu(spaceContainer, itemId, spaceName, true);
        AccountIconsStack.Children.Add(spaceContainer);
    }

    private Grid MakeDraggableContainer(string itemId, bool isInsideFolder = false)
    {
        var container = new Grid { Width = 44, Height = 40, Tag = itemId };
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });

        if (isInsideFolder) return container;

        container.CanDrag = true;
        container.AllowDrop = true;

        var capturedId = itemId;
        container.DragStarting += (s, e) =>
        {
            _draggedAccountId = capturedId;
            e.Data.RequestedOperation = DataPackageOperation.Move;
            ((Grid)s).Opacity = 0.4;
        };

        container.DropCompleted += (s, e) =>
        {
            ((Grid)s).Opacity = 1;
            _draggedAccountId = null;
        };

        container.DragOver += (s, e) =>
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            if (_draggedAccountId == null || _draggedAccountId == capturedId) return;
            if ((DateTime.UtcNow - _lastNetSwap).TotalMilliseconds < 350) return;

            if (capturedId.StartsWith("folder:") && !_draggedAccountId.StartsWith("folder:"))
                return;

            var draggedFromFolder = _folders.Values.FirstOrDefault(f => f.Items.Contains(_draggedAccountId));
            if (draggedFromFolder != null && !_sidebarOrder.Contains(_draggedAccountId))
            {
                _lastNetSwap = DateTime.UtcNow;
                draggedFromFolder.Items.Remove(_draggedAccountId);
                var toIdx = _sidebarOrder.IndexOf(capturedId);
                if (toIdx >= 0)
                    _sidebarOrder.Insert(toIdx, _draggedAccountId);
                else
                    _sidebarOrder.Add(_draggedAccountId);
                if (draggedFromFolder.Items.Count < 2)
                    DissolveFolder(draggedFromFolder.Id);
                SaveSidebarOrder();
                RenderAccountIcons();
                return;
            }

            var fromIdx = _sidebarOrder.IndexOf(_draggedAccountId);
            var toIdx2 = _sidebarOrder.IndexOf(capturedId);
            if (fromIdx >= 0 && toIdx2 >= 0 && fromIdx != toIdx2)
            {
                _lastNetSwap = DateTime.UtcNow;
                _sidebarOrder.RemoveAt(fromIdx);
                _sidebarOrder.Insert(toIdx2, _draggedAccountId);
                var visualFrom = FindVisualIndex(fromIdx);
                var visualTo = FindVisualIndex(toIdx2);
                if (visualFrom >= 0 && visualTo >= 0 && visualFrom < AccountIconsStack.Children.Count)
                {
                    var child = AccountIconsStack.Children[visualFrom];
                    AccountIconsStack.Children.RemoveAt(visualFrom);
                    if (visualTo > AccountIconsStack.Children.Count) visualTo = AccountIconsStack.Children.Count;
                    DispatcherQueue.TryEnqueue(() => AccountIconsStack.Children.Insert(visualTo, child));
                }
            }
        };

        container.Drop += (s, e) =>
        {
            if (_draggedAccountId != null && !_draggedAccountId.StartsWith("folder:")
                && capturedId.StartsWith("folder:") && _folders.TryGetValue(capturedId, out var targetFolder))
            {
                var dragId = _draggedAccountId;
                _sidebarOrder.Remove(dragId);
                foreach (var f in _folders.Values) f.Items.Remove(dragId);
                targetFolder.Items.Add(dragId);
                _draggedAccountId = null;
                SaveSidebarOrder();
                RenderAccountIcons();
                return;
            }
            SaveSidebarOrder();
            _draggedAccountId = null;
        };

        return container;
    }

    private int FindVisualIndex(int sidebarIdx)
    {
        int visual = 0;
        for (int i = 0; i < sidebarIdx && i < _sidebarOrder.Count; i++)
        {
            if (_sidebarOrder[i].StartsWith("folder:") && _expandedFolders.Contains(_sidebarOrder[i])
                && _folders.TryGetValue(_sidebarOrder[i], out var f))
                visual += 2 + f.Items.Count(id => !GetHiddenNetworks().Contains(id));
            else
                visual++;
        }
        return visual;
    }

    private static Border MakeSelIndicator(string tag) => new Border
    {
        Width = 3, Height = 18,
        CornerRadius = new CornerRadius(0, 2, 2, 0),
        Background = B(Accent),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
        Tag = tag
    };

    private void RenderFolderIcon(string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder)) return;
        var hidden = GetHiddenNetworks();
        var visibleItems = folder.Items.Where(id => !hidden.Contains(id)).ToList();

        if (_expandedFolders.Contains(folderId))
        {
            RenderExpandedFolder(folderId, folder, visibleItems);
            return;
        }

        var folderColor = GetFolderColor(folder);
        var container = MakeDraggableContainer(folderId);

        var selIndicator = MakeSelIndicator("folder_sel");
        Grid.SetColumn(selIndicator, 0);
        container.Children.Add(selIndicator);

        var miniGrid = new Grid
        {
            Width = 32, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Background = B(folderColor),
            Padding = new Thickness(2),
        };
        miniGrid.RowDefinitions.Add(new RowDefinition());
        miniGrid.RowDefinitions.Add(new RowDefinition());
        miniGrid.ColumnDefinitions.Add(new ColumnDefinition());
        miniGrid.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < 4; i++)
        {
            var cell = new Border
            {
                Background = B(Microsoft.UI.Colors.Transparent),
            };
            if (i < visibleItems.Count)
            {
                cell.Background = B(Microsoft.UI.Colors.Transparent);
                cell.Child = MakeMiniIcon(visibleItems[i], 12);
            }
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            miniGrid.Children.Add(cell);
        }

        Grid.SetColumn(miniGrid, 1);
        container.Children.Add(miniGrid);

        ToolTipService.SetToolTip(container, folder.Name);

        var capId = folderId;
        container.Tapped += (s, e) => ExpandFolderInPlace(capId);

        AddFolderContextMenu(container, folderId);
        AccountIconsStack.Children.Add(container);
    }

    private void RenderExpandedFolder(string folderId, SidebarFolder folder, List<string> visibleItems)
    {
        var folderColor = GetFolderColor(folder);
        var headerContainer = MakeFolderHeader(folderId, folderColor);

        var capId = folderId;
        headerContainer.Tapped += (s, e) => CollapseFolderInPlace(capId);

        AddFolderContextMenu(headerContainer, folderId);
        AccountIconsStack.Children.Add(headerContainer);

        foreach (var itemId in visibleItems)
            AccountIconsStack.Children.Add(MakeFolderItemWrapper(itemId, folderId));

        AccountIconsStack.Children.Add(new Border
        {
            Height = 6,
            Background = B(folderColor),
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Tag = $"cap:{folderId}",
            Margin = new Thickness(3, -4, 3, 0),
        });
    }

    private Grid MakeFolderItemWrapper(string itemId, string folderId)
    {
        var folderColor = _folders.TryGetValue(folderId, out var ff) ? GetFolderColor(ff) : Windows.UI.Color.FromArgb(255, 35, 35, 35);
        var wrapper = new Grid
        {
            AllowDrop = true,
            Tag = $"fi:{folderId}:{itemId}",
            Margin = new Thickness(0, -4, 0, 0),
        };
        wrapper.Children.Add(new Border
        {
            Background = B(folderColor),
            Margin = new Thickness(3, 0, 3, 0),
        });

        wrapper.DragOver += (s, e) =>
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            if (_draggedAccountId == null || _draggedAccountId.StartsWith("folder:")) return;
            if (!_folders.TryGetValue(folderId, out var f)) return;

            var dragInFolder = f.Items.Contains(_draggedAccountId);
            if (!dragInFolder) return;

            if ((DateTime.UtcNow - _lastNetSwap).TotalMilliseconds < 350) return;
            var fromIdx = f.Items.IndexOf(_draggedAccountId);
            var toIdx = f.Items.IndexOf(itemId);
            if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
            {
                _lastNetSwap = DateTime.UtcNow;
                f.Items.RemoveAt(fromIdx);
                f.Items.Insert(toIdx, _draggedAccountId);
                SaveFolders();

                var parent = (Panel)((FrameworkElement)s).Parent;
                if (parent == null) return;
                int vFrom = -1, vTo = -1;
                for (int i = 0; i < parent.Children.Count; i++)
                {
                    if (parent.Children[i] is Grid g)
                    {
                        var tag = g.Tag as string ?? "";
                        if (tag == $"fi:{folderId}:{_draggedAccountId}") vFrom = i;
                        if (tag == $"fi:{folderId}:{itemId}") vTo = i;
                    }
                }
                if (vFrom >= 0 && vTo >= 0 && vFrom != vTo)
                {
                    var child = parent.Children[vFrom];
                    parent.Children.RemoveAt(vFrom);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (vTo > parent.Children.Count) vTo = parent.Children.Count;
                        parent.Children.Insert(vTo, child);
                    });
                }
            }
        };

        var capItem = itemId;
        var capFolderId = folderId;
        wrapper.Drop += (s, e) =>
        {
            if (_draggedAccountId == null || _draggedAccountId == capItem || _draggedAccountId.StartsWith("folder:")) return;
            if (_folders.TryGetValue(capFolderId, out var f) && f.Items.Contains(_draggedAccountId))
            {
                SaveSidebarOrder();
                _draggedAccountId = null;
                return;
            }
            var dragId = _draggedAccountId;
            _draggedAccountId = null;
            _sidebarOrder.Remove(dragId);
            foreach (var of in _folders.Values) of.Items.Remove(dragId);
            if (f != null)
            {
                var insertAt = f.Items.IndexOf(capItem);
                if (insertAt >= 0) f.Items.Insert(insertAt, dragId);
                else f.Items.Add(dragId);
            }
            SaveSidebarOrder();
            RenderAccountIcons();
        };

        var innerContainer = MakeDraggableContainer(itemId, isInsideFolder: false);
        innerContainer.Width = 44;
        innerContainer.Height = 40;

        if (itemId.StartsWith("space:"))
            BuildSpaceIconContent(innerContainer, itemId);
        else
            BuildNetworkIconContent(innerContainer, itemId);

        AddFolderItemContextMenu(innerContainer, itemId, folderId);
        wrapper.Children.Add(innerContainer);
        return wrapper;
    }

    private void BuildNetworkIconContent(Grid container, string accountId)
    {
        var account = _accounts.FirstOrDefault(a => a.AccountId == accountId);
        if (account == null) return;

        var selIndicator = MakeSelIndicator("sel");
        Grid.SetColumn(selIndicator, 0);
        container.Children.Add(selIndicator);

        var iconBorder = NetIcon(account.AccountId, 32, account.Network);
        Grid.SetColumn(iconBorder, 1);
        container.Children.Add(iconBorder);

        var networkName = NetName(account.AccountId, account.Network);
        ToolTipService.SetToolTip(container, networkName);

        var capturedId = accountId;
        container.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            _selectedAccountId = capturedId;
            _selectedSpaceId = null;
            HighlightSelectedAccount();
            ChatList.FilterByAccount(capturedId);
        };

        container.PointerEntered += (s, _) => iconBorder.Opacity = 0.8;
        container.PointerExited += (s, _) => iconBorder.Opacity = 1.0;
    }

    private void BuildSpaceIconContent(Grid container, string itemId)
    {
        var parts = itemId.Split(':', 3);
        if (parts.Length < 3) return;
        var accountId = parts[1];
        var spaceId = parts[2];

        var spaceName = ChatList.GetSpaceName(accountId, spaceId) ?? "Server";
        var spaceAvatar = ChatList.GetSpaceAvatar(accountId, spaceId);

        var spaceSel = MakeSelIndicator("space_sel");
        Grid.SetColumn(spaceSel, 0);
        container.Children.Add(spaceSel);
        container.Tag = $"{accountId}:{spaceId}";

        var spaceContent = new Grid();
        var initial = spaceName.Length > 0 ? spaceName[0].ToString().ToUpper() : "?";
        spaceContent.Children.Add(new TextBlock
        {
            Text = initial, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
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
        container.Children.Add(spaceBorder);

        ToolTipService.SetToolTip(container, spaceName);

        var capAcct = accountId;
        var capSpace = spaceId;
        container.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            _selectedAccountId = capAcct;
            _selectedSpaceId = capSpace;
            HighlightSelectedAccount();
            ChatList.FilterByAccount(capAcct, capSpace);
        };
        container.PointerEntered += (s, _) => spaceBorder.Opacity = 0.8;
        container.PointerExited += (s, _) => spaceBorder.Opacity = 1.0;
    }

    private Windows.UI.Color GetItemColor(string itemId)
    {
        if (itemId.StartsWith("space:"))
            return Windows.UI.Color.FromArgb(255, 88, 101, 242);
        return NetColor(itemId);
    }

    private string GetItemInitial(string itemId)
    {
        if (itemId.StartsWith("space:"))
        {
            var parts = itemId.Split(':', 3);
            if (parts.Length >= 3)
            {
                var name = ChatList.GetSpaceName(parts[1], parts[2]);
                if (!string.IsNullOrEmpty(name)) return name[0].ToString().ToUpper();
            }
            return "?";
        }
        var acct = _accounts.FirstOrDefault(a => a.AccountId == itemId);
        var n = NetName(itemId, acct?.Network);
        return n.Length > 0 ? n[0].ToString().ToUpper() : "?";
    }

    private FrameworkElement MakeMiniIcon(string itemId, double size)
    {
        if (itemId.StartsWith("space:"))
        {
            var parts = itemId.Split(':', 3);
            var spaceAvatar = parts.Length >= 3 ? ChatList.GetSpaceAvatar(parts[1], parts[2]) : null;
            var initial = GetItemInitial(itemId);
            var content = new Grid();
            content.Children.Add(new TextBlock
            {
                Text = initial, FontSize = size * 0.5,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = B(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(spaceAvatar))
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                    { DecodePixelWidth = (int)(size * 2), DecodePixelHeight = (int)(size * 2) };
                try { bmp.UriSource = new Uri(spaceAvatar); } catch { }
                var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Opacity = 0 };
                img.ImageOpened += (s, _) =>
                {
                    ((Image)s).Opacity = 1;
                    if (((Image)s).Parent is Grid g && g.Children[0] is TextBlock tb)
                        tb.Visibility = Visibility.Collapsed;
                };
                img.ImageFailed += (s, _) => ((Image)s).Visibility = Visibility.Collapsed;
                content.Children.Add(img);
            }
            var border = new Border
            {
                Width = size, Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = B(Windows.UI.Color.FromArgb(255, 88, 101, 242)),
                Child = content,
            };
            border.Loaded += (s, _) =>
            {
                var b = (Border)s;
                b.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, size, size) };
            };
            return border;
        }

        var acct = _accounts.FirstOrDefault(a => a.AccountId == itemId);
        var icon = NetIcon(itemId, size, acct?.Network);
        return icon;
    }

    private Grid MakeFolderHeader(string folderId, Windows.UI.Color folderColor)
    {
        var headerContainer = MakeDraggableContainer(folderId);
        headerContainer.Height = 28;
        headerContainer.CanDrag = false;

        var headerBg = new Border
        {
            Background = B(folderColor),
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Margin = new Thickness(3, 0, 3, 0),
        };
        Grid.SetColumnSpan(headerBg, 3);
        headerContainer.Children.Add(headerBg);

        var headerIcon = new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 11,
            Foreground = B(Fg3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(headerIcon, 1);
        headerContainer.Children.Add(headerIcon);

        var capId = folderId;
        headerContainer.Tapped += (s, e) => CollapseFolderInPlace(capId);
        AddFolderContextMenu(headerContainer, folderId);
        return headerContainer;
    }

    private void AddFolderContextMenu(FrameworkElement element, string folderId)
    {
        var flyout = new MenuFlyout();

        var renameItem = new MenuFlyoutItem
        {
            Text = "Rename folder",
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameItem.Click += (s, e) => _ = RenameFolderAsync(folderId);
        flyout.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "Delete folder",
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        var capId = folderId;
        deleteItem.Click += (s, e) =>
        {
            DissolveFolder(capId);
            SaveSidebarOrder();
            RenderAccountIcons();
        };
        flyout.Items.Add(deleteItem);

        var colorSub = new MenuFlyoutSubItem
        {
            Text = "Folder color",
            Icon = new FontIcon { Glyph = "\uE790" }
        };
        foreach (var hex in FolderColors)
        {
            var capHex = hex;
            var swatch = ParseColor(hex);
            var colorItem = new MenuFlyoutItem
            {
                Text = "   ",
                Icon = new FontIcon
                {
                    Glyph = "\uEA3B",
                    FontSize = 16,
                    Foreground = B(swatch),
                }
            };
            colorItem.Click += (s, e) =>
            {
                if (_folders.TryGetValue(capId, out var f))
                {
                    f.Color = capHex;
                    SaveFolders();
                    RenderAccountIcons();
                }
            };
            colorSub.Items.Add(colorItem);
        }
        flyout.Items.Add(colorSub);

        element.ContextFlyout = flyout;
    }

    private void AddFolderItemContextMenu(FrameworkElement element, string itemId, string folderId)
    {
        var flyout = new MenuFlyout();

        var removeItem = new MenuFlyoutItem
        {
            Text = "Remove from folder",
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        var capItem = itemId;
        var capFolder = folderId;
        removeItem.Click += (s, e) =>
        {
            if (_folders.TryGetValue(capFolder, out var f))
            {
                f.Items.Remove(capItem);
                var idx = _sidebarOrder.IndexOf(capFolder);
                if (idx >= 0) _sidebarOrder.Insert(idx, capItem);
                else _sidebarOrder.Add(capItem);
                if (f.Items.Count < 2) DissolveFolder(capFolder);
                SaveSidebarOrder();
                RenderAccountIcons();
            }
        };
        flyout.Items.Add(removeItem);

        var name = itemId.StartsWith("space:") ? "server" : "network";
        var hideItem = new MenuFlyoutItem
        {
            Text = $"Hide {name}",
            Icon = new FontIcon { Glyph = "\uED1A" }
        };
        hideItem.Click += (s, e) =>
        {
            var h = GetHiddenNetworks();
            h.Add(capItem);
            SetHiddenNetworks(h);
            RenderAccountIcons();
            UpdateSettingsFlyout();
        };
        flyout.Items.Add(hideItem);

        element.ContextFlyout = flyout;
    }

    private async Task RenameFolderAsync(string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder)) return;
        var input = new TextBox { Text = folder.Name, SelectionStart = 0, SelectionLength = folder.Name.Length };
        var dialog = new ContentDialog
        {
            Title = "Rename folder",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            folder.Name = input.Text.Trim();
            SaveFolders();
            RenderAccountIcons();
        }
    }

    private void SetupEmptySpaceContextMenu()
    {
        if (AccountIconsStack.Parent is ScrollViewer sv && sv.ContextFlyout == null)
        {
            var flyout = new MenuFlyout();
            var newFolderItem = new MenuFlyoutItem
            {
                Text = "New folder",
                Icon = new FontIcon { Glyph = "\uE8F4" }
            };
            newFolderItem.Click += (s, e) =>
            {
                var id = CreateFolder();
                _sidebarOrder.Add(id);
                _expandedFolders.Add(id);
                SaveSidebarOrder();
                RenderAccountIcons();
                _ = RenameFolderAsync(id);
            };
            flyout.Items.Add(newFolderItem);
            sv.ContextFlyout = flyout;
        }
    }

    private void ExpandFolderInPlace(string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder)) return;
        for (int j = 0; j < AccountIconsStack.Children.Count; j++)
            if (AccountIconsStack.Children[j] is Border b && b.Tag as string == $"cap:{folderId}") return;
        _expandedFolders.Add(folderId);
        SaveFolders();

        int idx = -1;
        for (int i = 0; i < AccountIconsStack.Children.Count; i++)
        {
            if (AccountIconsStack.Children[i] is Grid g && g.Tag as string == folderId)
            { idx = i; break; }
        }
        if (idx < 0) return;

        AccountIconsStack.Children.RemoveAt(idx);

        var hidden = GetHiddenNetworks();
        var visibleItems = folder.Items.Where(id => !hidden.Contains(id)).ToList();

        var folderColor = GetFolderColor(folder);
        var headerContainer = MakeFolderHeader(folderId, folderColor);
        AccountIconsStack.Children.Insert(idx, headerContainer);

        int insertAt = idx + 1;
        var newElements = new List<UIElement>();
        foreach (var itemId in visibleItems)
        {
            var wrapper = MakeFolderItemWrapper(itemId, folderId);
            AccountIconsStack.Children.Insert(insertAt++, wrapper);
            newElements.Add(wrapper);
        }

        var cap = new Border
        {
            Height = 6,
            Background = B(folderColor),
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Tag = $"cap:{folderId}",
            Margin = new Thickness(3, -4, 3, 0),
        };
        AccountIconsStack.Children.Insert(insertAt, cap);
        newElements.Add(cap);

        foreach (var el in newElements)
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            visual.CenterPoint = new Vector3(22f, 0f, 0f);
            visual.Scale = new Vector3(1f, 0f, 1f);
            visual.Opacity = 0f;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            for (int i = 0; i < newElements.Count; i++)
            {
                var visual = ElementCompositionPreview.GetElementVisual(newElements[i]);
                var compositor = visual.Compositor;
                var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, Vector3.One, compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(250);
                scaleAnim.DelayTime = TimeSpan.FromMilliseconds(i * 25);
                var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
                opacityAnim.InsertKeyFrame(1f, 1f);
                opacityAnim.Duration = TimeSpan.FromMilliseconds(150);
                opacityAnim.DelayTime = TimeSpan.FromMilliseconds(i * 25);
                visual.StartAnimation("Scale", scaleAnim);
                visual.StartAnimation("Opacity", opacityAnim);
            }
        });
    }

    private void CollapseFolderInPlace(string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder)) return;
        bool hasCap = false;
        for (int j = 0; j < AccountIconsStack.Children.Count; j++)
            if (AccountIconsStack.Children[j] is Border b && b.Tag as string == $"cap:{folderId}") { hasCap = true; break; }
        if (!hasCap) return;
        _expandedFolders.Remove(folderId);
        SaveFolders();

        int headerIdx = -1;
        for (int i = 0; i < AccountIconsStack.Children.Count; i++)
        {
            if (AccountIconsStack.Children[i] is Grid g && g.Tag as string == folderId)
            { headerIdx = i; break; }
        }
        if (headerIdx < 0) return;

        var toRemove = new List<UIElement>();
        for (int i = headerIdx + 1; i < AccountIconsStack.Children.Count; i++)
        {
            toRemove.Add(AccountIconsStack.Children[i]);
            if (AccountIconsStack.Children[i] is Border b && b.Tag as string == $"cap:{folderId}")
                break;
        }

        for (int i = 0; i < toRemove.Count; i++)
            AccountIconsStack.Children.Remove(toRemove[i]);
        AccountIconsStack.Children.RemoveAt(headerIdx);

        FinishCollapse(folderId, headerIdx);
    }

    private void FinishCollapse(string folderId, int insertIdx)
    {
        if (!_folders.TryGetValue(folderId, out var folder)) return;
        for (int j = 0; j < AccountIconsStack.Children.Count; j++)
        {
            if (AccountIconsStack.Children[j] is Grid g && g.Tag as string == folderId)
                return;
        }
        var folderColor = GetFolderColor(folder);
        var hidden = GetHiddenNetworks();
        var visibleItems = folder.Items.Where(id => !hidden.Contains(id)).ToList();

        var container = MakeDraggableContainer(folderId);
        var selIndicator = MakeSelIndicator("folder_sel");
        Grid.SetColumn(selIndicator, 0);
        container.Children.Add(selIndicator);

        var miniGrid = new Grid
        {
            Width = 32, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Background = B(folderColor),
            Padding = new Thickness(2),
        };
        miniGrid.RowDefinitions.Add(new RowDefinition());
        miniGrid.RowDefinitions.Add(new RowDefinition());
        miniGrid.ColumnDefinitions.Add(new ColumnDefinition());
        miniGrid.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < 4; i++)
        {
            var cell = new Border
            {
                Background = B(Microsoft.UI.Colors.Transparent),
            };
            if (i < visibleItems.Count)
            {
                cell.Background = B(Microsoft.UI.Colors.Transparent);
                cell.Child = MakeMiniIcon(visibleItems[i], 12);
            }
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            miniGrid.Children.Add(cell);
        }
        Grid.SetColumn(miniGrid, 1);
        container.Children.Add(miniGrid);

        ToolTipService.SetToolTip(container, folder.Name);
        container.Tapped += (s, e) => ExpandFolderInPlace(folderId);
        AddFolderContextMenu(container, folderId);
        if (insertIdx > AccountIconsStack.Children.Count) insertIdx = AccountIconsStack.Children.Count;
        AccountIconsStack.Children.Insert(insertIdx, container);
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
            var target = child is Grid g && g.Children.Count == 1 && g.Children[0] is Grid inner ? inner : child as Grid;
            if (target == null) continue;
            foreach (var sub in target.Children)
            {
                if (sub is Border b && b.Tag is string tag)
                {
                    if (tag == "sel")
                    {
                        var acctId = target.Tag as string;
                        AnimateIndicator(b, acctId == _selectedAccountId && _selectedSpaceId == null);
                    }
                    else if (tag == "space_sel")
                    {
                        var spaceKey = target.Tag as string;
                        AnimateIndicator(b, spaceKey == $"{_selectedAccountId}:{_selectedSpaceId}");
                    }
                    else if (tag == "folder_sel")
                    {
                        var folderId = target.Tag as string;
                        var show = folderId != null && _folders.TryGetValue(folderId, out var f)
                            && f.Items.Any(id => id == _selectedAccountId
                                || (_selectedSpaceId != null && id == $"space:{_selectedAccountId}:{_selectedSpaceId}"));
                        AnimateIndicator(b, show);
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

    private void LoadFolders()
    {
        _folders.Clear();
        var json = App.Settings.GetString("sidebar_folders");
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<SidebarFolder>>(json);
                if (list != null)
                    foreach (var f in list) _folders[f.Id] = f;
            }
            catch { }
        }
        var exp = App.Settings.GetString("expanded_folders");
        _expandedFolders = string.IsNullOrEmpty(exp) ? [] : exp.Split(',').ToHashSet();
    }

    private void SaveFolders()
    {
        var list = _folders.Values.ToList();
        App.Settings.SetString("sidebar_folders", list.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(list) : null);
        App.Settings.SetString("expanded_folders", _expandedFolders.Count > 0
            ? string.Join(",", _expandedFolders) : null);
    }

    private void SaveSidebarOrder()
    {
        App.Settings.SetString("network_order", string.Join(",", _sidebarOrder));
        SaveFolders();
    }

    private string CreateFolder(string? name = null, params string[] initialItems)
    {
        var id = $"folder:{Guid.NewGuid():N}";
        var folder = new SidebarFolder { Id = id, Name = name ?? "Folder", Items = initialItems.ToList() };
        _folders[id] = folder;
        return id;
    }

    private void DissolveFolder(string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder)) return;
        var idx = _sidebarOrder.IndexOf(folderId);
        if (idx >= 0)
        {
            _sidebarOrder.RemoveAt(idx);
            foreach (var item in folder.Items)
                _sidebarOrder.Insert(idx++, item);
        }
        _folders.Remove(folderId);
        _expandedFolders.Remove(folderId);
    }

    private HashSet<string> GetAllFolderItemIds()
    {
        var set = new HashSet<string>();
        foreach (var f in _folders.Values)
            foreach (var item in f.Items) set.Add(item);
        return set;
    }

    private void AddNetworkContextMenu(FrameworkElement element, string id, string displayName, bool isSpace = false)
    {
        var flyout = new MenuFlyout();

        var hideItem = new MenuFlyoutItem
        {
            Text = $"Hide {displayName}",
            Icon = new FontIcon { Glyph = "\uED1A" }
        };
        var capId = id;
        hideItem.Click += (s, e) =>
        {
            var h = GetHiddenNetworks();
            h.Add(capId);
            SetHiddenNetworks(h);
            RenderAccountIcons();
            UpdateSettingsFlyout();
        };
        flyout.Items.Add(hideItem);

        if (_folders.Count > 0 || true)
        {
            var folderSub = new MenuFlyoutSubItem
            {
                Text = "Move to folder",
                Icon = new FontIcon { Glyph = "\uE8F4" }
            };

            foreach (var f in _folders.Values)
            {
                var capFolder = f;
                var moveItem = new MenuFlyoutItem { Text = f.Name };
                moveItem.Click += (s, e) =>
                {
                    _sidebarOrder.Remove(capId);
                    foreach (var of in _folders.Values) of.Items.Remove(capId);
                    capFolder.Items.Add(capId);
                    SaveSidebarOrder();
                    RenderAccountIcons();
                };
                folderSub.Items.Add(moveItem);
            }

            if (_folders.Count > 0)
                folderSub.Items.Add(new MenuFlyoutSeparator());

            var newFolderItem = new MenuFlyoutItem
            {
                Text = "New folder",
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            newFolderItem.Click += (s, e) =>
            {
                var idx = _sidebarOrder.IndexOf(capId);
                _sidebarOrder.Remove(capId);
                var folderId = CreateFolder("Folder", capId);
                if (idx >= 0 && idx <= _sidebarOrder.Count)
                    _sidebarOrder.Insert(idx, folderId);
                else
                    _sidebarOrder.Add(folderId);
                _expandedFolders.Add(folderId);
                SaveSidebarOrder();
                RenderAccountIcons();
                _ = RenameFolderAsync(folderId);
            };
            folderSub.Items.Add(newFolderItem);
            flyout.Items.Add(folderSub);
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

        var existing = SettingsFlyout.Items.OfType<MenuFlyoutSubItem>()
            .FirstOrDefault(i => i.Tag is string t && t == "hidden_sub");
        if (existing != null) SettingsFlyout.Items.Remove(existing);

        var hidden = GetHiddenNetworks();
        if (hidden.Count == 0) return;

        var sub = new MenuFlyoutSubItem
        {
            Text = $"Hidden ({hidden.Count})",
            Icon = new FontIcon { Glyph = "\uED1A" },
            Tag = "hidden_sub"
        };

        foreach (var id in hidden)
        {
            var name = ResolveHiddenName(id);
            var capturedId = id;
            var item = new MenuFlyoutItem
            {
                Text = name,
                Icon = new FontIcon { Glyph = "\uE7B3" }
            };
            item.Click += (s, e) =>
            {
                var h = GetHiddenNetworks();
                h.Remove(capturedId);
                SetHiddenNetworks(h);
                RenderAccountIcons();
                UpdateSettingsFlyout();
            };
            sub.Items.Add(item);
        }

        sub.Items.Add(new MenuFlyoutSeparator());
        var showAll = new MenuFlyoutItem
        {
            Text = "Show all",
            Icon = new FontIcon { Glyph = "\uE7B3" }
        };
        showAll.Click += (s, e) =>
        {
            SetHiddenNetworks([]);
            RenderAccountIcons();
            UpdateSettingsFlyout();
        };
        sub.Items.Add(showAll);

        var insertIdx = SettingsFlyout.Items.IndexOf(SettingsItem);
        if (insertIdx >= 0)
            SettingsFlyout.Items.Insert(insertIdx + 1, sub);
        else
            SettingsFlyout.Items.Add(sub);
    }

    private string ResolveHiddenName(string id)
    {
        if (id.StartsWith("space:"))
        {
            var parts = id.Split(':', 3);
            if (parts.Length >= 3)
                return ChatList.GetSpaceName(parts[1], parts[2]) ?? id;
        }
        var acct = _accounts.FirstOrDefault(a => a.AccountId == id);
        if (acct != null) return NetName(acct.AccountId, acct.Network);
        return NetName(id);
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
