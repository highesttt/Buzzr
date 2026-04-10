using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;
using BeeperWinUI.Services;
using static BeeperWinUI.Theme.T;

namespace BeeperWinUI.Views;

public sealed partial class ChatListControl : UserControl
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public event Action<BeeperChat>? ChatSelected;
    public event Action<string, string>? MessageReceived; // chatId, messageJson

    private string? _selectedChatId;
    private List<BeeperChat> _allChats = [];
    private Dictionary<string, BeeperAccount> _accountMap = [];
    private CancellationTokenSource? _searchCts;
    private string? _accountFilter;
    private string _tabFilter = "all";
    private int _focusIndex = -1;
    private List<BeeperChat> _displayedChats = [];
    private CancellationTokenSource? _wsCts;

    public ChatListControl()
    {
        this.InitializeComponent();

        SearchBox.TextChanged += (s, e) => _ = OnSearchChangedAsync();
        SearchBox.KeyDown += OnSearchKeyDown;

        TabAll.Click += (s, e) => SetTab("all");
        TabUnread.Click += (s, e) => SetTab("unread");
        TabPinned.Click += (s, e) => SetTab("pinned");
        TabArchived.Click += (s, e) => SetTab("archived");

        NewChatBtn.Click += (s, e) => ShellPage.OpenNewChat?.Invoke();

        _ = LoadAllChatsAsync();
    }

    public void SetAccounts(List<BeeperAccount> accounts)
    {
        _accountMap.Clear();
        foreach (var a in accounts)
            _accountMap[a.AccountId] = a;
    }

    public void FilterByAccount(string? accountId)
    {
        _accountFilter = accountId;
        ApplyFilters();
    }

    public void FocusSearchBox() => SearchBox.Focus(FocusState.Programmatic);

    private static void AnimateSelection(Border border, Color targetColor)
    {
        border.Background = B(targetColor);
        var visual = ElementCompositionPreview.GetElementVisual(border);
        var compositor = visual.Compositor;
        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.InsertKeyFrame(0f, 0.3f);
        fadeAnim.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        fadeAnim.Duration = TimeSpan.FromMilliseconds(200);
        visual.StartAnimation("Opacity", fadeAnim);
    }

    private void SetTab(string tab)
    {
        _tabFilter = tab;
        UpdateTabVisuals();
        var visual = ElementCompositionPreview.GetElementVisual(ListStack);
        var compositor = visual.Compositor;
        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f);
        fadeIn.Duration = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation("Opacity", fadeIn);
        ApplyFilters();
    }

    private void UpdateTabVisuals()
    {
        TabAll.Background = B(_tabFilter == "all" ? Selected : Colors.Transparent);
        TabAll.Foreground = B(_tabFilter == "all" ? Fg1 : Fg2);
        TabUnread.Background = B(_tabFilter == "unread" ? Selected : Colors.Transparent);
        TabUnread.Foreground = B(_tabFilter == "unread" ? Fg1 : Fg2);
        TabPinned.Background = B(_tabFilter == "pinned" ? Selected : Colors.Transparent);
        TabPinned.Foreground = B(_tabFilter == "pinned" ? Fg1 : Fg2);
        TabArchived.Background = B(_tabFilter == "archived" ? Selected : Colors.Transparent);
        TabArchived.Foreground = B(_tabFilter == "archived" ? Fg1 : Fg2);
    }

    private void ApplyFilters()
    {
        var filtered = _allChats.AsEnumerable();

        if (!string.IsNullOrEmpty(_accountFilter))
            filtered = filtered.Where(c => c.AccountId == _accountFilter);

        filtered = _tabFilter switch
        {
            "unread" => filtered.Where(c => c.UnreadCount > 0),
            "pinned" => filtered.Where(c => c.IsPinned),
            "archived" => filtered.Where(c => c.IsArchived),
            _ => filtered.Where(c => !c.IsArchived) // "all" excludes archived
        };

        RenderChats(filtered.ToList());
    }

    private async Task OnSearchChangedAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        try
        {
            await Task.Delay(250, token);
            if (token.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                ApplyFilters();
            }
            else
            {
                var query = SearchBox.Text;
                var tokenStr = App.Settings.AccessToken ?? "";
                var results = await Task.Run(() => {
                    using var http = new HttpClient();
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenStr);
                    var body = http.GetStringAsync($"http://localhost:23373/v1/chats/search?q={Uri.EscapeDataString(query)}").GetAwaiter().GetResult();
                    var resp = JsonSerializer.Deserialize<ChatsResponse>(body, s_jsonOptions);
                    return resp?.Chats ?? [];
                });
                if (!token.IsCancellationRequested)
                    RenderChats(results);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Down)
        {
            e.Handled = true;
            MoveFocus(1);
        }
        else if (e.Key == Windows.System.VirtualKey.Up)
        {
            e.Handled = true;
            MoveFocus(-1);
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (_focusIndex >= 0 && _focusIndex < _displayedChats.Count)
                SelectChat(_displayedChats[_focusIndex]);
        }
    }

    private void MoveFocus(int delta)
    {
        if (_displayedChats.Count == 0) return;
        var newIndex = _focusIndex + delta;
        if (newIndex < 0) newIndex = 0;
        if (newIndex >= _displayedChats.Count) newIndex = _displayedChats.Count - 1;
        _focusIndex = newIndex;
        HighlightFocused();
    }

    private void HighlightFocused()
    {
        for (int i = 0; i < ListStack.Children.Count; i++)
        {
            if (ListStack.Children[i] is Border b && b.Child is Grid)
            {
                var chatIdx = GetChatIndexFromBorder(i);
                if (chatIdx == _focusIndex)
                    b.Background = B(Hover);
                else if (_displayedChats.Count > chatIdx && chatIdx >= 0 &&
                         _displayedChats[chatIdx].Id == _selectedChatId)
                    b.Background = B(Selected);
                else
                    b.Background = Transparent;
            }
        }
    }

    private int GetChatIndexFromBorder(int childIndex)
    {
        // Divider borders don't have Grid children, so skip them
        int chatIdx = 0;
        for (int i = 0; i < childIndex && i < ListStack.Children.Count; i++)
        {
            if (ListStack.Children[i] is Border b && b.Child is Grid)
                chatIdx++;
        }
        return chatIdx;
    }

    private async Task LoadAllChatsAsync()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        LoadingText.Text = "Fetching chats...";

        try
        {
            // Run entire pagination off UI thread to avoid deadlock
            var allChats = await Task.Run(() =>
            {
                var chats = new List<BeeperChat>();
                string? cursor = null;
                bool hasMore = true;
                while (hasMore)
                {
                    using var http = new HttpClient();
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", App.Settings.AccessToken ?? "");
                    var url = "http://localhost:23373/v1/chats";
                    if (cursor != null) url += $"?cursor={Uri.EscapeDataString(cursor)}";
                    var body = http.GetStringAsync(url).GetAwaiter().GetResult();
                    var response = JsonSerializer.Deserialize<ChatsResponse>(body, s_jsonOptions);
                    if (response?.Chats == null || response.Chats.Count == 0) break;
                    chats.AddRange(response.Chats);
                    cursor = response.OldestCursor;
                    hasMore = response.HasMore && !string.IsNullOrEmpty(cursor);
                }
                return chats;
            });

            _allChats = allChats;
        }
        catch (Exception ex)
        {
            if (_allChats.Count == 0)
            {
                LoadingText.Text = $"Error: {ex.Message}";
                LoadingRing.IsActive = false;
                return;
            }
        }

        if (_allChats.Count == 0)
        {
            LoadingText.Text = $"Error: {App.Api.LastError ?? "No chats returned"}";
            LoadingRing.IsActive = false;
            return;
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;

        ApplyFilters();
        StartRealTimeUpdates();
    }

    private void RenderChats(List<BeeperChat> chats)
    {
        ListStack.Children.Clear();
        _focusIndex = -1;

        var sorted = chats
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.LastActivity ?? "")
            .ToList();

        _displayedChats = sorted;

        bool hadPinned = false;
        foreach (var chat in sorted)
        {
            if (chat.IsPinned && !hadPinned)
            {
                hadPinned = true;
            }
            else if (!chat.IsPinned && hadPinned)
            {
                hadPinned = false;
                ListStack.Children.Add(Divider());
            }
            ListStack.Children.Add(MakeChatRow(chat));
        }
    }

    private void SelectChat(BeeperChat chat)
    {
        _selectedChatId = chat.Id;
        foreach (var child in ListStack.Children)
        {
            if (child is Border b && b.Child is Grid)
                b.Background = Transparent;
        }
        for (int i = 0; i < ListStack.Children.Count; i++)
        {
            if (ListStack.Children[i] is Border b && b.Child is Grid)
            {
                var idx = GetChatIndexFromBorder(i);
                if (idx >= 0 && idx < _displayedChats.Count && _displayedChats[idx].Id == chat.Id)
                {
                    AnimateSelection(b, Selected);
                    break;
                }
            }
        }
        ChatSelected?.Invoke(chat);
    }

    private FrameworkElement MakeChatRow(BeeperChat chat)
    {
        var isSelected = chat.Id == _selectedChatId;

        var row = new Grid { Padding = new Thickness(8), ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatarGrid = new Grid { Width = 40, Height = 40 };
        var avatarElement = TryMakeAvatarElement(chat);
        avatarGrid.Children.Add(avatarElement);

        var netDot = NetDot(chat.AccountId, 12);
        netDot.HorizontalAlignment = HorizontalAlignment.Right;
        netDot.VerticalAlignment = VerticalAlignment.Bottom;
        netDot.BorderBrush = B(Surface);
        netDot.BorderThickness = new Thickness(2);
        netDot.CornerRadius = new CornerRadius(7);
        netDot.Width = 14; netDot.Height = 14;
        avatarGrid.Children.Add(netDot);
        Grid.SetColumn(avatarGrid, 0);
        row.Children.Add(avatarGrid);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (chat.IsPinned)
            titleStack.Children.Add(new FontIcon { Glyph = "\uE718", FontSize = 10, Foreground = B(Fg3), VerticalAlignment = VerticalAlignment.Center });
        if (chat.IsMuted)
            titleStack.Children.Add(new FontIcon { Glyph = "\uE74F", FontSize = 10, Foreground = B(Fg3), VerticalAlignment = VerticalAlignment.Center });
        titleStack.Children.Add(Lbl(chat.Title ?? "Unknown", 13, Fg1, true, maxLines: 1));
        titleRow.Children.Add(titleStack);

        var time = Lbl(RelativeTime(chat.LastActivity), 11, Fg3);
        Grid.SetColumn(time, 1);
        titleRow.Children.Add(time);
        info.Children.Add(titleRow);

        var networkName = GetNetworkName(chat);
        if (!string.IsNullOrEmpty(networkName))
            info.Children.Add(Lbl(networkName, 11, Fg3, margin: new Thickness(0, 1, 0, 0), maxLines: 1));

        var previewText = chat.Preview?.Text ?? "";
        if (previewText.Length > 60) previewText = previewText[..60] + "...";
        if (!string.IsNullOrEmpty(previewText))
            info.Children.Add(Lbl(previewText, 12, Fg2, maxLines: 1, margin: new Thickness(0, 2, 0, 0)));

        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var rightStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        if (chat.UnreadCount > 0)
            rightStack.Children.Add(Badge(chat.UnreadCount));
        Grid.SetColumn(rightStack, 2);
        row.Children.Add(rightStack);

        var border = new Border
        {
            Child = row, CornerRadius = new CornerRadius(4),
            Background = B(isSelected ? Selected : Colors.Transparent),
            Opacity = chat.IsMuted ? 0.7 : 1.0
        };

        border.PointerEntered += (s, _) =>
        {
            if (chat.Id != _selectedChatId) AnimateSelection((Border)s, Hover);
        };
        border.PointerExited += (s, _) =>
        {
            ((Border)s).Background = B(chat.Id == _selectedChatId ? Selected : Colors.Transparent);
        };
        border.PointerPressed += (_, _) => SelectChat(chat);

        var flyout = new MenuFlyout();

        var pinItem = new MenuFlyoutItem
        {
            Text = chat.IsPinned ? "Unpin" : "Pin",
            Icon = new FontIcon { Glyph = chat.IsPinned ? "\uE77A" : "\uE718" }
        };
        pinItem.Click += (s, e) => _ = TogglePinAsync(chat);
        flyout.Items.Add(pinItem);

        var muteItem = new MenuFlyoutItem
        {
            Text = chat.IsMuted ? "Unmute" : "Mute",
            Icon = new FontIcon { Glyph = chat.IsMuted ? "\uE767" : "\uE74F" }
        };
        muteItem.Click += (s, e) => _ = ToggleMuteAsync(chat);
        flyout.Items.Add(muteItem);

        var archiveItem = new MenuFlyoutItem
        {
            Text = chat.IsArchived ? "Unarchive" : "Archive",
            Icon = new FontIcon { Glyph = "\uE7B8" }
        };
        archiveItem.Click += (s, e) => _ = ToggleArchiveAsync(chat);
        flyout.Items.Add(archiveItem);

        if (chat.UnreadCount > 0)
        {
            var markReadItem = new MenuFlyoutItem
            {
                Text = "Mark as read",
                Icon = new FontIcon { Glyph = "\uE73E" }
            };
            markReadItem.Click += (s, e) => _ = MarkAsReadAsync(chat);
            flyout.Items.Add(markReadItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var reminderSub = new MenuFlyoutSubItem
        {
            Text = "Remind me",
            Icon = new FontIcon { Glyph = "\uE787" }
        };

        var remind30m = new MenuFlyoutItem { Text = "In 30 minutes" };
        remind30m.Click += (s, e) => _ = SetChatReminderAsync(chat, TimeSpan.FromMinutes(30));
        reminderSub.Items.Add(remind30m);

        var remind1h = new MenuFlyoutItem { Text = "In 1 hour" };
        remind1h.Click += (s, e) => _ = SetChatReminderAsync(chat, TimeSpan.FromHours(1));
        reminderSub.Items.Add(remind1h);

        var remind3h = new MenuFlyoutItem { Text = "In 3 hours" };
        remind3h.Click += (s, e) => _ = SetChatReminderAsync(chat, TimeSpan.FromHours(3));
        reminderSub.Items.Add(remind3h);

        var remindTomorrow = new MenuFlyoutItem { Text = "Tomorrow morning (9 AM)" };
        remindTomorrow.Click += (s, e) =>
        {
            var tomorrow9am = DateTime.Today.AddDays(1).AddHours(9);
            var ms = new DateTimeOffset(tomorrow9am).ToUnixTimeMilliseconds();
            _ = SetChatReminderFromMsAsync(chat, ms);
        };
        reminderSub.Items.Add(remindTomorrow);

        flyout.Items.Add(reminderSub);

        border.ContextFlyout = flyout;

        return border;
    }

    private FrameworkElement TryMakeAvatarElement(BeeperChat chat)
    {
        var participants = chat.Participants?.Items;
        if (participants == null || participants.Count == 0)
            return Avatar(chat.Title ?? "?", 40, NetColor(chat.AccountId));

        var nonSelf = participants.Where(p => !p.IsSelf).ToList();

        if (chat.Type == "group" && nonSelf.Count >= 2)
        {
            var grid = new Grid { Width = 40, Height = 40 };
            var first = MakeSmallAvatar(nonSelf[0], 26);
            first.HorizontalAlignment = HorizontalAlignment.Left;
            first.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(first);
            var second = MakeSmallAvatar(nonSelf[1], 26);
            second.HorizontalAlignment = HorizontalAlignment.Right;
            second.VerticalAlignment = VerticalAlignment.Bottom;
            grid.Children.Add(second);
            return grid;
        }

        var person = nonSelf.FirstOrDefault() ?? participants.FirstOrDefault();
        if (person != null && !string.IsNullOrEmpty(person.ImgUrl))
            return TryMakeAvatarImage(person.ImgUrl, 40, chat.Title ?? "?", chat.AccountId);

        return Avatar(chat.Title ?? "?", 40, NetColor(chat.AccountId));
    }

    private static FrameworkElement MakeSmallAvatar(BeeperUser user, double size)
    {
        if (!string.IsNullOrEmpty(user.ImgUrl))
        {
            try
            {
                var bmp = new BitmapImage(new Uri(user.ImgUrl));
                bmp.DecodePixelWidth = (int)size;
                bmp.DecodePixelHeight = (int)size;
                var img = new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.UniformToFill };
                return new Border
                {
                    Width = size, Height = size,
                    CornerRadius = new CornerRadius(size / 2),
                    Child = img,
                    BorderBrush = B(Surface),
                    BorderThickness = new Thickness(2)
                };
            }
            catch { }
        }
        var name = user.FullName ?? user.DisplayText ?? user.Username ?? "?";
        var av = Avatar(name, size);
        av.BorderBrush = B(Surface);
        av.BorderThickness = new Thickness(2);
        return av;
    }

    private static Border TryMakeAvatarImage(string imgUrl, double size, string fallbackName, string accountId)
    {
        try
        {
            var bmp = new BitmapImage(new Uri(imgUrl));
            bmp.DecodePixelWidth = (int)size;
            bmp.DecodePixelHeight = (int)size;
            var img = new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.UniformToFill };
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = img };
        }
        catch
        {
            return Avatar(fallbackName, size, NetColor(accountId));
        }
    }

    private string GetNetworkName(BeeperChat chat)
    {
        if (_accountMap.TryGetValue(chat.AccountId, out var acct) && !string.IsNullOrEmpty(acct.Network))
        {
            var n = acct.Network;
            return char.ToUpper(n[0]) + n[1..];
        }
        return NetName(chat.AccountId);
    }

    private async Task TogglePinAsync(BeeperChat chat)
    {
        try
        {
            if (chat.IsPinned)
                await App.Api.UnpinChatAsync(chat.Id);
            else
                await App.Api.PinChatAsync(chat.Id);
            chat.IsPinned = !chat.IsPinned;
            ApplyFilters();
        }
        catch { }
    }

    private async Task ToggleMuteAsync(BeeperChat chat)
    {
        try
        {
            if (chat.IsMuted)
                await App.Api.UnmuteChatAsync(chat.Id);
            else
                await App.Api.MuteChatAsync(chat.Id);
            chat.IsMuted = !chat.IsMuted;
            ApplyFilters();
        }
        catch { }
    }

    private async Task ToggleArchiveAsync(BeeperChat chat)
    {
        try
        {
            if (chat.IsArchived)
                await App.Api.UnarchiveChatAsync(chat.Id);
            else
                await App.Api.ArchiveChatAsync(chat.Id);
            chat.IsArchived = !chat.IsArchived;
            ApplyFilters();
        }
        catch { }
    }

    private async Task MarkAsReadAsync(BeeperChat chat)
    {
        try
        {
            await App.Api.MarkChatReadAsync(chat.Id);
            chat.UnreadCount = 0;
            ApplyFilters();
        }
        catch { }
    }

    private static async Task SetChatReminderAsync(BeeperChat chat, TimeSpan offset)
    {
        try
        {
            var remindAtMs = DateTimeOffset.Now.Add(offset).ToUnixTimeMilliseconds();
            await App.Api.SetReminderAsync(chat.Id, new SetReminderInput
            {
                Reminder = new ReminderData
                {
                    RemindAtMs = remindAtMs,
                    DismissOnIncomingMessage = false
                }
            });
        }
        catch { }
    }

    private static async Task SetChatReminderFromMsAsync(BeeperChat chat, long remindAtMs)
    {
        try
        {
            await App.Api.SetReminderAsync(chat.Id, new SetReminderInput
            {
                Reminder = new ReminderData
                {
                    RemindAtMs = remindAtMs,
                    DismissOnIncomingMessage = false
                }
            });
        }
        catch { }
    }

    private void StartRealTimeUpdates()
    {
        _wsCts?.Cancel();
        _wsCts = new CancellationTokenSource();
        var cts = _wsCts;

        var token = App.Settings.AccessToken ?? "";
        if (string.IsNullOrEmpty(token)) return;

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                    await ws.ConnectAsync(new Uri("ws://localhost:23373/v1/ws"), cts.Token);

                    var subMsg = Encoding.UTF8.GetBytes(
                        "{\"type\":\"subscriptions.set\",\"chatIDs\":[\"*\"]}");
                    await ws.SendAsync(new ArraySegment<byte>(subMsg),
                        WebSocketMessageType.Text, true, cts.Token);

                    var buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        using var ms = new MemoryStream();
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var json = Encoding.UTF8.GetString(ms.ToArray());
                        HandleWsMessage(json);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (!cts.Token.IsCancellationRequested)
                        await Task.Delay(3000, cts.Token).ConfigureAwait(false);
                }
            }
        });
    }

    private void HandleWsMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var evtType = typeProp.GetString() ?? "";

            if (evtType == "chat.upserted")
            {
                if (root.TryGetProperty("chat", out var chatEl))
                {
                    var chat = JsonSerializer.Deserialize<BeeperChat>(chatEl.GetRawText(), s_jsonOptions);
                    if (chat != null)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            UpsertChatInList(chat);
                            ApplyFilters();
                        });
                    }
                }
            }
            else if (evtType == "message.upserted")
            {
                string? chatId = null;
                if (root.TryGetProperty("message", out var msgEl) &&
                    msgEl.TryGetProperty("chatID", out var cidEl))
                {
                    chatId = cidEl.GetString();
                }
                else if (root.TryGetProperty("chatID", out var cidEl2))
                {
                    chatId = cidEl2.GetString();
                }

                if (!string.IsNullOrEmpty(chatId))
                {
                    _ = RefreshChat(chatId);
                    var msgJson = root.TryGetProperty("message", out var mjEl) ? mjEl.GetRawText() : "";
                    DispatcherQueue.TryEnqueue(() => MessageReceived?.Invoke(chatId, msgJson));
                }
            }
        }
        catch { }
    }

    public async Task RefreshChat(string chatId)
    {
        try
        {
            var updated = await Task.Run(() =>
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer",
                        App.Settings.AccessToken ?? "");
                var body = http.GetStringAsync(
                    $"http://localhost:23373/v1/chats/{Uri.EscapeDataString(chatId)}")
                    .GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<BeeperChat>(body, s_jsonOptions);
            });

            if (updated != null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpsertChatInList(updated);
                    ApplyFilters();
                });
            }
        }
        catch { }
    }

    private void UpsertChatInList(BeeperChat chat)
    {
        var idx = _allChats.FindIndex(c => c.Id == chat.Id);
        if (idx >= 0)
            _allChats[idx] = chat;
        else
            _allChats.Insert(0, chat);
    }
}
