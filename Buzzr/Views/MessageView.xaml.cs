using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Buzzr.Services;
using static Buzzr.Theme.T;

namespace Buzzr.Views;

public sealed partial class MessageView : UserControl
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private string? _chatId;
    private BeeperChat? _currentChat;
    private Dictionary<string, BeeperUser> _participantMap = [];
    private Dictionary<string, BeeperMessage> _messageMap = [];
    private List<BeeperMessage> _allMessages = [];
    private string? _msgCursor;
    private bool _msgHasMore;
    private bool _isLoadingMore;

    private string? _editingMessageId;
    private string? _replyToMessageId;

    private CancellationTokenSource? _chatSearchCts;
    private CancellationTokenSource? _loadChatCts;

    private static readonly ConcurrentDictionary<string, string> _resolvedUrlCache = new();
    private readonly ConcurrentDictionary<string, BeeperMessage> _replyCache = new();

    private Flyout? _gifFlyout;
    private CancellationTokenSource? _gifSearchCts;

    private class StagedAttachment
    {
        public string FileName { get; set; } = "";
        public string MimeType { get; set; } = "";
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public string? LocalPath { get; set; }
        public BitmapImage? Thumbnail { get; set; }
        public bool IsImage => MimeType.StartsWith("image/");
    }
    private readonly List<StagedAttachment> _stagedAttachments = [];

    private class ScheduledMessage
    {
        public string ChatId { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTimeOffset SendAt { get; set; }
        public List<StagedAttachment> Attachments { get; set; } = [];
    }
    private static readonly List<ScheduledMessage> _scheduledMessages = [];
    private readonly DispatcherTimer _scheduleTimer;

    public MessageView()
    {
        this.InitializeComponent();

        MsgInput.TextChanged += (s, e) =>
        {
            SendBtn.IsEnabled = !string.IsNullOrWhiteSpace(MsgInput.Text) || _stagedAttachments.Count > 0;
        };

        SendBtn.Click += (s, e) => _ = SendAsync();

        MsgInput.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (!shift) { e.Handled = true; _ = SendAsync(); }
            }
        };

        MsgInput.Paste += (s, e) =>
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                e.Handled = true;
                _ = PasteImageAsync(content);
            }
        };

        AttachBtn.Click += (s, e) => _ = PickAndAttachAsync();

        EmojiBtn.Click += (s, e) =>
        {
            MsgInput.Focus(FocusState.Programmatic);
            try
            {
                var inputs = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo[4];
                inputs[0] = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
                {
                    VirtualKey = (ushort)Windows.System.VirtualKey.LeftWindows,
                    KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.None
                };
                inputs[1] = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
                {
                    VirtualKey = 0xBE,
                    KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.None
                };
                inputs[2] = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
                {
                    VirtualKey = 0xBE,
                    KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.KeyUp
                };
                inputs[3] = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
                {
                    VirtualKey = (ushort)Windows.System.VirtualKey.LeftWindows,
                    KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.KeyUp
                };
                var injector = Windows.UI.Input.Preview.Injection.InputInjector.TryCreate();
                if (injector != null)
                    injector.InjectKeyboardInput(inputs);
            }
            catch
            { }
        };

        GifBtn.Click += (s, e) => ToggleGifPicker();
        EditCancelBtn.Click += (s, e) => CancelEdit();
        ReplyCancelBtn.Click += (s, e) => CancelReply();
        SearchInChatBtn.Click += (s, e) => ToggleChatSearch();
        ChatSearchClose.Click += (s, e) => CloseChatSearch();
        ChatSearchBox.TextChanged += (s, e) => _ = OnChatSearchChangedAsync();
        MsgScroll.ViewChanged += OnScrollViewChanged;

        ScheduleBtn.Click += (s, e) => ShowScheduleFlyout();

        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += (s, e) => _ = ProcessScheduledMessagesAsync();
        _scheduleTimer.Start();
    }

    public void CloseOverlays()
    {
        CloseChatSearch();
        CancelEdit();
        CancelReply();
    }

    public void OnNewMessage(string chatId, string messageJson)
    {
        if (chatId != _chatId || string.IsNullOrEmpty(messageJson)) return;
        try
        {
            var msg = JsonSerializer.Deserialize<BeeperMessage>(messageJson, _json);
            if (msg == null || msg.Type == "REACTION") return;

            if (_messageMap.ContainsKey(msg.Id))
            {
                var existing = _messageMap[msg.Id];
                existing.Text = msg.Text;
                existing.Reactions = msg.Reactions;
                existing.Attachments = msg.Attachments;
                existing.Type = msg.Type;
                UpdateBubbleForMessage(existing);
                return;
            }

            if (string.IsNullOrEmpty(msg.Text) && (msg.Attachments == null || msg.Attachments.Count == 0)
                && msg.Type == "TEXT") return;

            _allMessages.Add(msg);
            _messageMap[msg.Id] = msg;
            _replyCache[msg.Id] = msg;

            var lastSender = _allMessages.Count > 1 ? _allMessages[^2].SenderId : null;
            var lastTs = _allMessages.Count > 1 ? _allMessages[^2].Timestamp : null;
            bool isGrouped = msg.SenderId == lastSender && IsWithinMinutes(lastTs, msg.Timestamp, 2);
            bool showSender = !msg.IsSender && !isGrouped;

            var dateStr = DateHeader(msg.Timestamp);
            var prevDate = _allMessages.Count > 1 ? DateHeader(_allMessages[^2].Timestamp) : null;
            if (dateStr != prevDate && !string.IsNullOrEmpty(dateStr))
            {
                MsgStack.Children.Add(new Border
                {
                    Background = B(SurfaceAlt),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(0, 16, 0, 8),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = Lbl(dateStr, 11, Fg3, false, HorizontalAlignment.Center)
                });
            }

            MsgStack.Children.Add(MakeBubble(msg, showSender, isGrouped));
            if (MsgStack.Children[^1] is FrameworkElement newBubble)
                AnimateBubbleIn(newBubble);
            ScrollToBottom();
        }
        catch { }
    }

    private void UpdateBubbleForMessage(BeeperMessage msg)
    {
        for (int i = 0; i < MsgStack.Children.Count; i++)
        {
            if (MsgStack.Children[i] is FrameworkElement el && el.Tag as string == msg.Id)
            {
                var msgIndex = _allMessages.FindIndex(m => m.Id == msg.Id);
                string? prevSender = msgIndex > 0 ? _allMessages[msgIndex - 1].SenderId : null;
                string? prevTs = msgIndex > 0 ? _allMessages[msgIndex - 1].Timestamp : null;
                bool isGrouped = msg.SenderId == prevSender && IsWithinMinutes(prevTs, msg.Timestamp, 2);
                bool showSender = !msg.IsSender && !isGrouped;
                MsgStack.Children[i] = MakeBubble(msg, showSender, isGrouped);
                return;
            }
        }
    }

    private void ToggleChatSearch()
    {
        if (ChatSearchBar.Visibility == Visibility.Visible)
            CloseChatSearch();
        else
        {
            ChatSearchBar.Visibility = Visibility.Visible;
            ChatSearchBox.Text = "";
            ChatSearchBox.Focus(FocusState.Programmatic);
        }
    }

    private void CloseChatSearch()
    {
        ChatSearchBar.Visibility = Visibility.Collapsed;
        ChatSearchBox.Text = "";
        if (_allMessages.Count > 0)
            RenderMessages(_allMessages);
    }

    private async Task OnChatSearchChangedAsync()
    {
        _chatSearchCts?.Cancel();
        _chatSearchCts = new CancellationTokenSource();
        var token = _chatSearchCts.Token;
        try
        {
            await Task.Delay(250, token);
            if (token.IsCancellationRequested) return;
            var query = ChatSearchBox.Text?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                RenderMessages(_allMessages);
            }
            else
            {
                var filtered = _allMessages
                    .Where(m => m.Text != null && m.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                RenderMessages(filtered);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate && MsgScroll.VerticalOffset < 50 && _msgHasMore && !_isLoadingMore)
        {
            _ = LoadEarlierMessagesAsync();
        }
    }

    public async Task LoadChat(BeeperChat chat)
    {
        _chatId = chat.Id;
        _loadChatCts?.Cancel();
        _loadChatCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _currentChat = chat;
        _editingMessageId = null;
        _replyToMessageId = null;
        _allMessages.Clear();
        _msgCursor = null;
        _msgHasMore = false;

        _participantMap.Clear();
        if (chat.Participants?.Items != null)
        {
            foreach (var p in chat.Participants.Items)
                _participantMap[p.Id] = p;
        }

        HeaderTitle.Text = chat.Title ?? "Chat";
        HeaderSubtitle.Text = NetName(chat.AccountId);
        HeaderNetDot.Background = B(NetColor(chat.AccountId));
        HeaderNetDot.Visibility = Visibility.Visible;
        SearchInChatBtn.Visibility = Visibility.Visible;

        UpdateHeaderAvatar(chat);

        if (chat.Type == "group" && chat.Participants != null)
        {
            var total = chat.Participants.Total > 0 ? chat.Participants.Total : (chat.Participants.Items?.Count ?? 0);
            HeaderParticipants.Text = $"  {total} members";
            HeaderParticipants.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderParticipants.Visibility = Visibility.Collapsed;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        MsgScroll.Visibility = Visibility.Collapsed;
        Composer.Visibility = Visibility.Visible;
        EditBar.Visibility = Visibility.Collapsed;
        ReplyBar.Visibility = Visibility.Collapsed;
        ChatSearchBar.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;

        StopAllMediaPlayers();
        MsgStack.Children.Clear();
        _stagedAttachments.Clear();
        AttachmentPreview.Visibility = Visibility.Collapsed;
        AttachmentPreviewStack.Children.Clear();

        var chatIdCopy = _chatId;
        var token = App.Settings.AccessToken ?? "";
        MessagesResponse? response = null;
        try
        {
            response = await App.Api.GetMessagesAsync(chatIdCopy);
        }
        catch (Exception)
        {
            if (chatIdCopy != _chatId) return;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            MsgScroll.Visibility = Visibility.Visible;
            var retryStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 8 };
            retryStack.Children.Add(Lbl("Failed to load messages", 14, Fg2, false, HorizontalAlignment.Center));
            var retryBtn = new Button { Content = "Retry", HorizontalAlignment = HorizontalAlignment.Center, Background = B(SurfaceAlt), Foreground = B(Accent), BorderThickness = new Thickness(0), Padding = new Thickness(16, 6, 16, 6), CornerRadius = new CornerRadius(14) };
            retryBtn.Click += (s, e) => _ = LoadChat(chat);
            retryStack.Children.Add(retryBtn);
            MsgStack.Children.Add(retryStack);
            return;
        }

        if (chatIdCopy != _chatId) return;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        MsgScroll.Visibility = Visibility.Visible;

        if (response?.Messages == null || response.Messages.Count == 0)
        {
            MsgStack.Children.Add(Lbl("No messages yet", 13, Fg3, false, HorizontalAlignment.Center, new Thickness(0, 20, 0, 0)));
            return;
        }

        var sorted = response.Messages.OrderBy(m => m.SortKey).ToList();
        _msgCursor = response.OldestCursor ?? response.Cursor ?? sorted.FirstOrDefault()?.SortKey;
        _msgHasMore = response.HasMore;
        _allMessages = response.Messages;

        _messageMap.Clear();
        foreach (var m in _allMessages)
        {
            _messageMap[m.Id] = m;
            _replyCache[m.Id] = m;
        }

        RenderMessages(_allMessages);
        ScrollToBottom();
        UpdateScheduledBar();

        _ = App.Api.MarkChatReadAsync(chat.Id);
    }

    private async Task LoadEarlierMessagesAsync()
    {
        if (_chatId == null || !_msgHasMore || _isLoadingMore || string.IsNullOrEmpty(_msgCursor)) return;

        _isLoadingMore = true;

        var prevHeight = MsgScroll.ScrollableHeight;

        var loadingIndicator = new ProgressRing { IsActive = true, Width = 24, Height = 24, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        MsgStack.Children.Insert(0, loadingIndicator);

        try
        {
            var chatIdCopy = _chatId;
            var cursorCopy = _msgCursor;
            var tokenCopy = App.Settings.AccessToken ?? "";
            var response = await Task.Run(async () =>
            {
                return await App.Api.GetMessagesAsync(chatIdCopy, cursor: cursorCopy, direction: "before");
            });
            MsgStack.Children.Remove(loadingIndicator);

            if (response?.Messages != null && response.Messages.Count > 0)
            {
                var olderSorted = response.Messages.OrderBy(m => m.SortKey).ToList();
                _msgCursor = response.OldestCursor ?? response.Cursor ?? olderSorted.FirstOrDefault()?.SortKey;
                _msgHasMore = response.HasMore;

                _allMessages.InsertRange(0, response.Messages);
                foreach (var m in response.Messages)
                {
                    _messageMap[m.Id] = m;
                    _replyCache[m.Id] = m;
                }

                var olderByTime = response.Messages
                    .Where(m => m.Type != "REACTION")
                    .Where(m => !(string.IsNullOrEmpty(m.Text) && (m.Attachments == null || m.Attachments.Count == 0) && m.Type == "TEXT"))
                    .OrderBy(m => m.Timestamp)
                    .ToList();

                int insertIdx = 0;
                string? lastSender = null;
                string? lastTimestamp = null;

                foreach (var m in olderByTime)
                {
                    var isOwn = m.IsSender;
                    var sender = m.SenderName ?? m.SenderId;
                    bool isGrouped = m.SenderId == lastSender && IsWithinMinutes(lastTimestamp, m.Timestamp, 2);
                    bool showSender = !isOwn && !isGrouped;

                    MsgStack.Children.Insert(insertIdx, MakeBubble(m, showSender, isGrouped));
                    insertIdx++;
                    lastSender = m.SenderId;
                    lastTimestamp = m.Timestamp;
                }

                MsgScroll.DispatcherQueue.TryEnqueue(() =>
                {
                    MsgScroll.UpdateLayout();
                    var newHeight = MsgScroll.ScrollableHeight;
                    MsgScroll.ChangeView(null, newHeight - prevHeight, null, true);
                });
            }
        }
        catch { MsgStack.Children.Remove(loadingIndicator); }

        _isLoadingMore = false;
    }

    private void RenderMessages(List<BeeperMessage> messages)
    {
        MsgStack.Children.Clear();

        var msgs = messages.OrderBy(m => m.Timestamp).ToList();
        string? lastDate = null;
        string? lastSender = null;
        string? lastTimestamp = null;

        if (_msgHasMore && messages == _allMessages)
        {
            var loadBtn = new Button
            {
                Content = "Load earlier messages",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8),
                Background = B(SurfaceAlt),
                Foreground = B(Accent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(16, 6, 16, 6),
                CornerRadius = new CornerRadius(14)
            };
            loadBtn.Click += (s, e) => _ = LoadEarlierMessagesAsync();
            MsgStack.Children.Add(loadBtn);
        }

        foreach (var m in msgs)
        {
            if (m.Type == "REACTION") continue;

            if (string.IsNullOrEmpty(m.Text) && (m.Attachments == null || m.Attachments.Count == 0)
                && m.Type == "TEXT") continue;

            var dateStr = DateHeader(m.Timestamp);
            if (dateStr != lastDate && !string.IsNullOrEmpty(dateStr))
            {
                MsgStack.Children.Add(new Border
                {
                    Background = B(SurfaceAlt),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(0, 16, 0, 8),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = Lbl(dateStr, 11, Fg3, false, HorizontalAlignment.Center)
                });
                lastDate = dateStr;
                lastSender = null;
                lastTimestamp = null;
            }

            var isOwn = m.IsSender;
            bool isGrouped = m.SenderId == lastSender && IsWithinMinutes(lastTimestamp, m.Timestamp, 2);
            bool showSender = !isOwn && !isGrouped;

            MsgStack.Children.Add(MakeBubble(m, showSender, isGrouped));
            lastSender = m.SenderId;
            lastTimestamp = m.Timestamp;
        }
    }

    private void UpdateHeaderAvatar(BeeperChat chat)
    {
        HeaderAvatarContainer.Child = null;
        HeaderAvatarContainer.Visibility = Visibility.Collapsed;

        var avatarElement = ChatListControl.MakeAvatarPublic(chat, 36);
        HeaderAvatarContainer.Child = avatarElement;
        HeaderAvatarContainer.Visibility = Visibility.Visible;
    }

    private FrameworkElement MakeBubble(BeeperMessage m, bool showSender, bool isGrouped)
    {
        var isOwn = m.IsSender;
        var sender = m.SenderName ?? m.SenderId;

        if (m.Type == "NOTICE")
        {
            return new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
                Padding = new Thickness(12, 4, 12, 4),
                Child = new TextBlock
                {
                    Text = m.Text ?? "System message",
                    FontSize = 12,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Foreground = B(Fg3),
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
        }

        var outerGrid = new Grid();
        outerGrid.Tag = m.Id;
        outerGrid.HorizontalAlignment = isOwn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        outerGrid.Margin = new Thickness(isOwn ? 60 : 0, isGrouped ? 1 : 8, isOwn ? 0 : 60, 1);
        outerGrid.MaxWidth = 520;

        if (!isOwn)
        {
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (!isGrouped)
            {
                var avatarElement = TryMakeSenderAvatar(m.SenderId, sender);
                avatarElement.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(avatarElement, 0);
                outerGrid.Children.Add(avatarElement);
            }
        }
        else
        {
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var content = new StackPanel { Spacing = 2 };

        if (showSender)
            content.Children.Add(Lbl(sender, 11, Accent, true));

        if (!string.IsNullOrEmpty(m.LinkedMessageId))
            content.Children.Add(BuildReplyBar(m.LinkedMessageId, isOwn));

        if (m.Attachments != null && m.Attachments.Count > 0)
        {
            foreach (var att in m.Attachments)
                content.Children.Add(BuildAttachment(att, isOwn));
        }

        var text = StripSenderPrefix(m.Text, m);
        if (!string.IsNullOrEmpty(text))
        {
            var tb = new TextBlock
            {
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = B(isOwn ? SentFg : Fg1),
                FontFamily = new FontFamily("Segoe UI"),
                IsTextSelectionEnabled = true
            };
            foreach (var inline in ParseMarkdownInlines(text, isOwn))
                tb.Inlines.Add(inline);
            content.Children.Add(tb);
        }
        else if (m.Attachments == null || m.Attachments.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = m.Type switch
                {
                    "IMAGE" => "[Image]",
                    "VIDEO" => "[Video]",
                    "VOICE" or "AUDIO" => "[Voice message]",
                    "FILE" => "[File]",
                    "STICKER" => "[Sticker]",
                    "LOCATION" => "[Location]",
                    _ => "[Message]"
                },
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = B(isOwn ? SentFg : Fg2),
                FontFamily = new FontFamily("Segoe UI"),
                FontStyle = Windows.UI.Text.FontStyle.Italic
            });
        }

        content.Children.Add(Lbl(MessageTime(m.Timestamp), 10, isOwn ? SentFg : Fg3,
            margin: new Thickness(0, 2, 0, 0)));

        if (m.Reactions?.Count > 0)
        {
            var rxRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            var grouped = m.Reactions.GroupBy(r => r.DisplayEmoji);
            foreach (var g in grouped)
            {
                rxRow.Children.Add(new Border
                {
                    Background = B(Surface),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6, 2, 6, 2),
                    Child = Lbl($"{g.Key} {g.Count()}", 11, Fg2)
                });
            }
            content.Children.Add(rxRow);
        }

        var bubble = new Border
        {
            Background = B(isOwn ? SentBg : SurfaceAlt),
            CornerRadius = new CornerRadius(isOwn ? 12 : 4, 12, isOwn ? 4 : 12, 12),
            Padding = new Thickness(12, 8, 12, 8),
            Child = content
        };

        bubble.ContextFlyout = BuildMessageContextMenu(m);

        if (!isOwn)
            Grid.SetColumn(bubble, 2);

        outerGrid.Children.Add(bubble);
        return outerGrid;
    }

    private MenuFlyout BuildMessageContextMenu(BeeperMessage m)
    {
        var flyout = new MenuFlyout();

        var replyItem = new MenuFlyoutItem { Text = "Reply", Icon = new FontIcon { Glyph = "\uE97A" } };
        replyItem.Click += (s, e) => StartReply(m);
        flyout.Items.Add(replyItem);

        var reactSub = new MenuFlyoutSubItem { Text = "React", Icon = new FontIcon { Glyph = "\uE76E" } };
        var commonEmojis = new[]
        {
            "\u2764\uFE0F", "\uD83D\uDC4D", "\uD83D\uDC4E", "\uD83D\uDE02", "\uD83D\uDE4F",
            "\uD83D\uDE0D", "\uD83D\uDE0E", "\uD83D\uDE2E", "\uD83D\uDE22", "\uD83E\uDD14",
            "\uD83E\uDD23", "\uD83D\uDE31", "\uD83D\uDE44",
            "\uD83D\uDC4F", "\uD83D\uDCAA", "\uD83D\uDE4C", "\u270C\uFE0F",
            "\uD83D\uDD25", "\uD83C\uDF89", "\u2705"
        };
        foreach (var emoji in commonEmojis)
        {
            var emojiItem = new MenuFlyoutItem { Text = emoji };
            var capturedEmoji = emoji;
            emojiItem.Click += (s, e) => _ = AddReactionAsync(m, capturedEmoji);
            reactSub.Items.Add(emojiItem);
        }
        reactSub.Items.Add(new MenuFlyoutSeparator());
        var moreItem = new MenuFlyoutItem { Text = "More... (Win+.)" };
        moreItem.Click += (s, e) =>
        {
            MsgInput.Focus(FocusState.Programmatic);
            try
            {
                var inputs = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo[4];
                inputs[0] = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
                {
                    VirtualKey = (ushort)Windows.System.VirtualKey.LeftWindows,
                    KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.None
                };
                inputs[1] = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
                {
                    VirtualKey = 0xBE,
                    KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.None
                };
                inputs[2] = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
                {
                    VirtualKey = 0xBE,
                    KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.KeyUp
                };
                inputs[3] = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
                {
                    VirtualKey = (ushort)Windows.System.VirtualKey.LeftWindows,
                    KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.KeyUp
                };
                var injector = Windows.UI.Input.Preview.Injection.InputInjector.TryCreate();
                if (injector != null)
                    injector.InjectKeyboardInput(inputs);
            }
            catch { }
        };
        reactSub.Items.Add(moreItem);
        flyout.Items.Add(reactSub);

        if (!string.IsNullOrEmpty(m.Text))
        {
            var copyItem = new MenuFlyoutItem { Text = "Copy text", Icon = new FontIcon { Glyph = "\uE8C8" } };
            copyItem.Click += (s, e) =>
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(m.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            };
            flyout.Items.Add(copyItem);
        }

        if (m.IsSender)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var editItem = new MenuFlyoutItem { Text = "Edit", Icon = new FontIcon { Glyph = "\uE70F" } };
            editItem.Click += (s, e) => StartEdit(m);
            flyout.Items.Add(editItem);

            var deleteItem = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "\uE74D" } };
            deleteItem.Click += (s, e) => _ = DeleteMessageAsync(m);
            flyout.Items.Add(deleteItem);
        }

        return flyout;
    }

    private void StartReply(BeeperMessage m)
    {
        CancelEdit();
        _replyToMessageId = m.Id;
        ReplyBarSender.Text = m.SenderName ?? m.SenderId;
        ReplyBarText.Text = m.Text ?? $"[{m.Type}]";
        ReplyBar.Visibility = Visibility.Visible;
        MsgInput.Focus(FocusState.Programmatic);
    }

    private void CancelReply()
    {
        _replyToMessageId = null;
        ReplyBar.Visibility = Visibility.Collapsed;
    }

    private void StartEdit(BeeperMessage m)
    {
        CancelReply();
        _editingMessageId = m.Id;
        EditBarText.Text = "Editing message";
        EditBar.Visibility = Visibility.Visible;
        MsgInput.Text = m.Text ?? "";
        MsgInput.Focus(FocusState.Programmatic);
    }

    private void CancelEdit()
    {
        _editingMessageId = null;
        EditBar.Visibility = Visibility.Collapsed;
        MsgInput.Text = "";
    }

    private async Task AddReactionAsync(BeeperMessage m, string emoji)
    {
        if (_chatId == null) return;
        m.Reactions ??= [];
        m.Reactions.Add(new BeeperReaction { ReactionKey = emoji, ParticipantID = "self" });
        UpdateBubbleForMessage(m);
        try
        {
            await App.Api.AddReactionAsync(_chatId, m.Id, emoji);
        }
        catch
        {
            m.Reactions.RemoveAll(r => r.ReactionKey == emoji && r.ParticipantID == "self");
            UpdateBubbleForMessage(m);
        }
    }

    private async Task DeleteMessageAsync(BeeperMessage m)
    {
        if (_chatId == null) return;
        try
        {
            var ok = await App.Api.DeleteMessageAsync(_chatId, m.Id);
            if (ok)
            {
                _allMessages.RemoveAll(x => x.Id == m.Id);
                _messageMap.Remove(m.Id);
                RenderMessages(_allMessages);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Delete] Failed: {App.Api.LastError}");
            }
        }
        catch { }
    }

    private FrameworkElement BuildReplyBar(string linkedMessageId, bool isOwn)
    {
        string replyText = "...";
        string replySender = "";

        BeeperMessage? replyMsg = null;
        if (!_messageMap.TryGetValue(linkedMessageId, out replyMsg))
            _replyCache.TryGetValue(linkedMessageId, out replyMsg);

        if (replyMsg != null)
        {
            replyText = StripSenderPrefix(replyMsg.Text, replyMsg) ?? $"[{replyMsg.Type}]";
            if (replyText.Length > 80) replyText = replyText[..80] + "...";
            replySender = replyMsg.SenderName ?? replyMsg.SenderId;
        }

        var replyStack = new StackPanel { Spacing = 1 };
        var senderLabel = Lbl(replySender, 10, Accent, true);
        var textLabel = Lbl(replyText, 11, isOwn ? SentFg : Fg2, maxLines: 2);

        if (!string.IsNullOrEmpty(replySender))
            replyStack.Children.Add(senderLabel);
        replyStack.Children.Add(textLabel);

        var replyBorder = new Border
        {
            BorderBrush = B(Accent),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 4),
            Background = B(isOwn ? AccentDark : Surface),
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            Child = replyStack
        };

        if (replyMsg == null && _chatId != null)
        {
            _ = FetchAndUpdateReplyAsync(linkedMessageId, senderLabel, textLabel, isOwn);
        }

        var capturedId = linkedMessageId;
        replyBorder.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            _ = ScrollToMessageAsync(capturedId);
        };

        return replyBorder;
    }

    private FrameworkElement BuildAttachment(BeeperAttachment att, bool isOwn)
    {
        if (att.IsVoiceNote)
        {
            var voiceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            voiceRow.Children.Add(new FontIcon { Glyph = "\uE767", FontSize = 16, Foreground = B(isOwn ? SentFg : Accent), VerticalAlignment = VerticalAlignment.Center });
            voiceRow.Children.Add(Lbl("[Voice message]", 13, isOwn ? SentFg : Fg2));

            return new Border
            {
                Background = B(isOwn ? AccentDark : Surface),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 2, 0, 2),
                Child = voiceRow
            };
        }

        if (att.IsSticker)
            return TryMakeImageElement(att, 128, 128, isOwn);

        var mimeType = att.MimeType ?? "";
        var attType = att.Type ?? "";
        if (mimeType.StartsWith("image/") || attType == "IMAGE" || att.IsGif)
        {
            var w = att.GetWidth();
            var h = att.GetHeight();
            int maxW = 300, maxH = 300;
            if (w > 0 && h > 0)
            {
                var ratio = Math.Min((double)maxW / w, (double)maxH / h);
                if (ratio < 1) { w = (int)(w * ratio); h = (int)(h * ratio); }
            }
            else { w = 200; h = 150; }
            return TryMakeImageElement(att, w, h, isOwn);
        }

        if (mimeType.StartsWith("video/") || attType == "VIDEO")
        {
            var srcUrl = att.SrcURL;
            if (string.IsNullOrEmpty(srcUrl))
            {
                var stubStack = new StackPanel { Spacing = 4 };
                stubStack.Children.Add(new FontIcon { Glyph = "\uE714", FontSize = 24, Foreground = B(isOwn ? SentFg : Fg2) });
                stubStack.Children.Add(Lbl(att.FileName ?? "Video", 12, isOwn ? SentFg : Fg2, maxLines: 1));
                if (att.FileSize.HasValue)
                    stubStack.Children.Add(Lbl(FormatFileSize(att.FileSize.Value), 10, isOwn ? SentFg : Fg3));
                return new Border
                {
                    Background = B(isOwn ? AccentDark : Surface),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 2, 0, 2),
                    Child = stubStack
                };
            }

            var vw = att.GetWidth();
            var vh = att.GetHeight();
            int vmaxW = 300, vmaxH = 250;
            if (vw > 0 && vh > 0)
            {
                var ratio = Math.Min((double)vmaxW / vw, (double)vmaxH / vh);
                if (ratio < 1) { vw = (int)(vw * ratio); vh = (int)(vh * ratio); }
            }
            else { vw = 280; vh = 200; }

            var videoContainer = new Border
            {
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 2, 0, 2),
                Width = vw,
                Height = vh,
                Background = B(isOwn ? AccentDark : Surface)
            };

            var playOverlay = new Grid { Width = vw, Height = vh };

            var loadRing = new ProgressRing
            {
                IsActive = false, Width = 24, Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var playBtn = new Button
            {
                Width = 48, Height = 48,
                CornerRadius = new CornerRadius(24),
                Background = B(Windows.UI.Color.FromArgb(180, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Content = new FontIcon
                {
                    Glyph = "\uE768",
                    FontSize = 20,
                    Foreground = B(Windows.UI.Color.FromArgb(255, 255, 255, 255))
                }
            };

            var infoRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 8)
            };
            infoRow.Children.Add(new FontIcon { Glyph = "\uE714", FontSize = 12, Foreground = B(Windows.UI.Color.FromArgb(255, 255, 255, 255)) });
            var videoLabel = att.FileName ?? "Video";
            if (att.FileSize.HasValue)
                videoLabel += $" ({FormatFileSize(att.FileSize.Value)})";
            infoRow.Children.Add(Lbl(videoLabel, 10, Windows.UI.Color.FromArgb(255, 255, 255, 255), maxLines: 1));

            playOverlay.Children.Add(loadRing);
            playOverlay.Children.Add(playBtn);
            playOverlay.Children.Add(infoRow);
            videoContainer.Child = playOverlay;

            var capturedSrcUrl = srcUrl;
            var capturedW = vw;
            var capturedH = vh;
            playBtn.Click += (s, e) => _ = PlayVideoAsync(videoContainer, capturedSrcUrl, capturedW, capturedH, loadRing, playBtn);

            return videoContainer;
        }

        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileRow.Children.Add(new FontIcon { Glyph = "\uE7C3", FontSize = 20, Foreground = B(isOwn ? SentFg : Accent), VerticalAlignment = VerticalAlignment.Center });
        var fileInfo = new StackPanel { Spacing = 2 };
        fileInfo.Children.Add(Lbl(att.FileName ?? "File", 12, isOwn ? SentFg : Fg1, true, maxLines: 1));
        if (att.FileSize.HasValue)
            fileInfo.Children.Add(Lbl(FormatFileSize(att.FileSize.Value), 10, isOwn ? SentFg : Fg3));
        fileRow.Children.Add(fileInfo);

        return new Border
        {
            Background = B(isOwn ? AccentDark : Surface),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 2),
            Child = fileRow
        };
    }

    private static async Task<string?> ResolveAssetUrlAsync(string srcUrl)
    {
        if (string.IsNullOrEmpty(srcUrl)) return null;
        if (srcUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || srcUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || srcUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return srcUrl;
        if (_resolvedUrlCache.TryGetValue(srcUrl, out var cached))
            return cached;
        try
        {
            var result = await Task.Run(async () => await App.Api.DownloadAssetAsync(srcUrl));
            var resolved = result?.SrcURL ?? BeeperApiService.GetAssetUrl(srcUrl);
            _resolvedUrlCache[srcUrl] = resolved;
            return resolved;
        }
        catch
        {
            var fallback = BeeperApiService.GetAssetUrl(srcUrl);
            _resolvedUrlCache[srcUrl] = fallback;
            return fallback;
        }
    }

    private FrameworkElement TryMakeImageElement(BeeperAttachment att, int width, int height, bool isOwn)
    {
        var srcUrl = att.SrcURL;
        if (string.IsNullOrEmpty(srcUrl))
            return Lbl("[Image]", 12, isOwn ? SentFg : Fg2);

        var container = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 2, 0, 2),
            MinWidth = Math.Min(width > 0 ? width : 200, 300),
            MinHeight = Math.Min(height > 0 ? height : 100, 300),
            Background = B(isOwn ? AccentDark : Surface)
        };

        var placeholder = new ProgressRing
        {
            IsActive = true,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        container.Child = placeholder;

        if (_resolvedUrlCache.TryGetValue(srcUrl, out var cachedUrl))
        {
            SetImageOnContainer(container, cachedUrl, width, isOwn);
        }
        else if (srcUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
              || srcUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
              || srcUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            SetImageOnContainer(container, srcUrl, width, isOwn);
        }
        else
        {
            _ = LoadImageAsync(container, srcUrl, width, isOwn);
        }

        return container;
    }

    private void SetImageOnContainer(Border container, string resolvedUrl, int width, bool isOwn)
    {
        try
        {
            var bmp = new BitmapImage(new Uri(resolvedUrl));
            if (width > 0) bmp.DecodePixelWidth = width;
            var img = new Image
            {
                Source = bmp,
                MaxWidth = 300,
                MaxHeight = 300,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            bmp.ImageOpened += (s, e) => ScrollToBottomIfNearEnd();
            container.Background = null;
            container.MinWidth = 0;
            container.MinHeight = 0;
            container.Child = img;
            var launchUrl = resolvedUrl;
            container.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(launchUrl)); }
                catch { }
            };
        }
        catch
        {
            container.Child = Lbl("[Image failed to load]", 12, isOwn ? SentFg : Fg2);
        }
    }

    private async Task LoadImageAsync(Border container, string srcUrl, int width, bool isOwn)
    {
        try
        {
            var resolvedUrl = await ResolveAssetUrlAsync(srcUrl);
            if (string.IsNullOrEmpty(resolvedUrl))
            {
                DispatcherQueue.TryEnqueue(() => container.Child = Lbl("[Image]", 12, isOwn ? SentFg : Fg2));
                return;
            }
            DispatcherQueue.TryEnqueue(() => SetImageOnContainer(container, resolvedUrl, width, isOwn));
        }
        catch
        {
            DispatcherQueue.TryEnqueue(() => container.Child = Lbl("[Image failed to load]", 12, isOwn ? SentFg : Fg2));
        }
    }

    private FrameworkElement TryMakeSenderAvatar(string senderId, string senderName)
    {
        if (_participantMap.TryGetValue(senderId, out var user) && !string.IsNullOrEmpty(user.ImgUrl))
        {
            try
            {
                var bmp = new BitmapImage(new Uri(user.ImgUrl));
                bmp.DecodePixelWidth = 28;
                bmp.DecodePixelHeight = 28;
                var img = new Image
                {
                    Source = bmp,
                    Width = 28,
                    Height = 28,
                    Stretch = Stretch.UniformToFill
                };
                return new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Child = img
                };
            }
            catch { }
        }
        return Avatar(senderName, 28);
    }

    private static bool IsWithinMinutes(string? ts1, string? ts2, int minutes)
    {
        if (string.IsNullOrEmpty(ts1) || string.IsNullOrEmpty(ts2)) return false;
        if (!DateTimeOffset.TryParse(ts1, out var d1) || !DateTimeOffset.TryParse(ts2, out var d2)) return false;
        return Math.Abs((d2 - d1).TotalMinutes) <= minutes;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }

    private string? StripSenderPrefix(string? text, BeeperMessage m)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(m.SenderName)) candidates.Add(m.SenderName);
        if (!string.IsNullOrEmpty(m.SenderId)) candidates.Add(m.SenderId);

        if (_participantMap.TryGetValue(m.SenderId, out var user))
        {
            if (!string.IsNullOrEmpty(user.FullName)) candidates.Add(user.FullName);
            if (!string.IsNullOrEmpty(user.Username)) candidates.Add(user.Username);
            if (!string.IsNullOrEmpty(user.DisplayText)) candidates.Add(user.DisplayText);
        }

        foreach (var name in candidates)
        {
            if (text.StartsWith(name + ": ", StringComparison.OrdinalIgnoreCase))
                return text[(name.Length + 2)..];
        }

        return text;
    }

    private async Task PlayVideoAsync(Border container, string srcUrl, int width, int height,
        ProgressRing loadRing, Button playBtn)
    {
        playBtn.Visibility = Visibility.Collapsed;
        loadRing.IsActive = true;

        try
        {
            var resolvedUrl = await ResolveAssetUrlAsync(srcUrl);
            if (string.IsNullOrEmpty(resolvedUrl))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    playBtn.Visibility = Visibility.Visible;
                    loadRing.IsActive = false;
                });
                return;
            }

            var capturedUrl = resolvedUrl;
            var capturedW = width;
            var capturedH = height;
            DispatcherQueue.TryEnqueue(() =>
            {
                loadRing.IsActive = false;

                var mediaPlayer = new Windows.Media.Playback.MediaPlayer();
                mediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(capturedUrl));

                var playerElement = new MediaPlayerElement
                {
                    Width = capturedW,
                    Height = capturedH,
                    AreTransportControlsEnabled = true,
                    Stretch = Stretch.Uniform
                };
                playerElement.TransportControls = new MediaTransportControls
                {
                    IsCompact = true,
                    IsZoomButtonVisible = false,
                    IsZoomEnabled = false
                };
                playerElement.SetMediaPlayer(mediaPlayer);

                container.Child = playerElement;
                mediaPlayer.Play();
            });
        }
        catch
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                playBtn.Visibility = Visibility.Visible;
                loadRing.IsActive = false;
            });
        }
    }

    private void StopAllMediaPlayers()
    {
        foreach (var child in MsgStack.Children)
        {
            Border? border = child as Border;
            if (border == null && child is Grid g)
            {
                foreach (var gc in g.Children)
                {
                    if (gc is Border b) border = b;
                }
            }
            if (border?.Child is MediaPlayerElement mpe)
            {
                try
                {
                    mpe.MediaPlayer?.Pause();
                    mpe.MediaPlayer?.Dispose();
                    mpe.SetMediaPlayer(null);
                }
                catch { }
            }
        }
    }

    private async Task FetchAndUpdateReplyAsync(string messageId, TextBlock senderLabel, TextBlock textLabel, bool isOwn)
    {
        if (_chatId == null) return;
        var chatId = _chatId;

        try
        {
            var cursor = _msgCursor;
            for (int page = 0; page < 5 && !string.IsNullOrEmpty(cursor); page++)
            {
                var cursorCopy = cursor;
                var response = await Task.Run(async () =>
                    await App.Api.GetMessagesAsync(chatId, cursor: cursorCopy, direction: "before"));

                if (response?.Messages == null || response.Messages.Count == 0) break;

                foreach (var m in response.Messages)
                    _replyCache[m.Id] = m;

                var found = response.Messages.FirstOrDefault(m => m.Id == messageId);
                if (found != null)
                {
                    if (chatId != _chatId) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var text = StripSenderPrefix(found.Text, found) ?? $"[{found.Type}]";
                        if (text.Length > 80) text = text[..80] + "...";
                        textLabel.Text = text;
                        senderLabel.Text = found.SenderName ?? found.SenderId;
                    });
                    return;
                }

                cursor = response.OldestCursor ?? response.Cursor;
            }
        }
        catch { }
    }

    private async Task ScrollToMessageAsync(string messageId)
    {
        for (int i = 0; i < MsgStack.Children.Count; i++)
        {
            if (MsgStack.Children[i] is FrameworkElement el && el.Tag as string == messageId)
            {
                el.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.3
                });
                FlashHighlight(el);
                return;
            }
        }

        for (int attempt = 0; attempt < 10 && _msgHasMore; attempt++)
        {
            await LoadEarlierMessagesAsync();

            for (int i = 0; i < MsgStack.Children.Count; i++)
            {
                if (MsgStack.Children[i] is FrameworkElement el && el.Tag as string == messageId)
                {
                    el.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalAlignmentRatio = 0.3
                    });
                    FlashHighlight(el);
                    return;
                }
            }
        }
    }

    private void FlashHighlight(FrameworkElement el)
    {
        if (el is Grid grid && grid.ColumnDefinitions.Count > 0)
        {
            var highlight = new Border
            {
                Background = B(Windows.UI.Color.FromArgb(40, 76, 194, 255)),
                CornerRadius = new CornerRadius(12),
                IsHitTestVisible = false
            };
            Grid.SetColumnSpan(highlight, grid.ColumnDefinitions.Count);
            grid.Children.Insert(0, highlight);

            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { grid.Children.Remove(highlight); } catch { }
                });
            });
        }
    }

    private void ScrollToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MsgScroll.UpdateLayout();
            MsgScroll.ChangeView(null, MsgScroll.ScrollableHeight, null);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                MsgScroll.UpdateLayout();
                MsgScroll.ChangeView(null, MsgScroll.ScrollableHeight, null);
            });
            _ = Task.Delay(300).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MsgScroll.UpdateLayout();
                    MsgScroll.ChangeView(null, MsgScroll.ScrollableHeight, null);
                });
            });
        });
    }

    private void ScrollToBottomIfNearEnd()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var distFromBottom = MsgScroll.ScrollableHeight - MsgScroll.VerticalOffset;
            if (distFromBottom < 100)
            {
                MsgScroll.UpdateLayout();
                MsgScroll.ChangeView(null, MsgScroll.ScrollableHeight, null);
            }
        });
    }

    private async Task SendAsync()
    {
        if (_chatId == null) return;
        var hasText = !string.IsNullOrWhiteSpace(MsgInput.Text);
        var hasAttachments = _stagedAttachments.Count > 0;
        if (!hasText && !hasAttachments) return;
        var text = MsgInput.Text?.Trim() ?? "";
        MsgInput.Text = "";
        SendBtn.IsEnabled = false;

        if (!string.IsNullOrEmpty(_editingMessageId))
        {
            var editId = _editingMessageId;
            CancelEdit();
            try
            {
                await App.Api.EditMessageAsync(_chatId, editId, text);
                if (_messageMap.TryGetValue(editId, out var editMsg))
                    editMsg.Text = text;
                RenderMessages(_allMessages);
            }
            catch { }
            MsgInput.Focus(FocusState.Programmatic);
            return;
        }

        string? replyId = _replyToMessageId;
        CancelReply();

        if (hasAttachments)
        {
            var attachments = _stagedAttachments.ToList();
            _stagedAttachments.Clear();
            RenderStagedAttachments();

            for (int i = 0; i < attachments.Count; i++)
            {
                var att = attachments[i];
                try
                {
                    var base64 = Convert.ToBase64String(att.Bytes);
                    var asset = await App.Api.UploadBase64Async(base64, att.FileName, att.MimeType);
                    if (asset?.UploadID != null)
                    {
                        var caption = (i == attachments.Count - 1 && hasText) ? text : null;
                        var attachResult = await App.Api.SendMessageWithAttachmentAsync(
                            _chatId, caption, asset.UploadID, att.MimeType, att.FileName);

                        var resolvedSrc = att.LocalPath;
                        if (!string.IsNullOrEmpty(resolvedSrc) && !resolvedSrc.StartsWith("file://"))
                            resolvedSrc = "file:///" + resolvedSrc.Replace("\\", "/");
                        if (string.IsNullOrEmpty(resolvedSrc)) resolvedSrc = asset.SrcURL;

                        var msgType = att.IsImage ? "IMAGE" : "FILE";
                        var optimistic = new BeeperMessage
                        {
                            Text = caption,
                            IsSender = true,
                            Timestamp = DateTimeOffset.Now.ToString("o"),
                            Type = msgType,
                            SenderName = "You",
                            Attachments =
                            [
                                new BeeperAttachment { FileName = att.FileName, MimeType = att.MimeType, SrcURL = resolvedSrc }
                            ]
                        };
                        if (attachResult?.PendingMessageID != null)
                        {
                            optimistic.Id = attachResult.PendingMessageID;
                            _messageMap[optimistic.Id] = optimistic;
                        }
                        _allMessages.Add(optimistic);
                        var bubble = MakeBubble(optimistic, false, false);
                        MsgStack.Children.Add(bubble);
                        AnimateBubbleIn(bubble);
                        ScrollToBottom();
                    }
                }
                catch { }
            }
        }
        else if (hasText)
        {
            var optimistic = new BeeperMessage
            {
                Text = text,
                IsSender = true,
                Timestamp = DateTimeOffset.Now.ToString("o"),
                Type = "TEXT",
                SenderName = "You",
                LinkedMessageId = replyId
            };
            _allMessages.Add(optimistic);
            MsgStack.Children.Add(MakeBubble(optimistic, false, false));
            ScrollToBottom();

            var sendResult = await App.Api.SendMessageAsync(_chatId, text, replyId);
            if (sendResult?.PendingMessageID != null && string.IsNullOrEmpty(optimistic.Id))
            {
                optimistic.Id = sendResult.PendingMessageID;
                _messageMap[optimistic.Id] = optimistic;
            }
        }
        MsgInput.Focus(FocusState.Programmatic);
    }

    private async Task PickAndAttachAsync()
    {
        if (_chatId == null) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
            var bytes = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                reader.ReadBytes(bytes);

            var mimeType = file.ContentType ?? "application/octet-stream";
            var staged = new StagedAttachment
            {
                FileName = file.Name,
                MimeType = mimeType,
                Bytes = bytes,
                LocalPath = file.Path
            };

            if (staged.IsImage)
            {
                try
                {
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    var writer = new Windows.Storage.Streams.DataWriter(stream);
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    stream.Seek(0);
                    staged.Thumbnail = new BitmapImage();
                    staged.Thumbnail.DecodePixelWidth = 80;
                    await staged.Thumbnail.SetSourceAsync(stream);
                }
                catch { }
            }

            _stagedAttachments.Add(staged);
            RenderStagedAttachments();
            SendBtn.IsEnabled = true;
            MsgInput.Focus(FocusState.Programmatic);
        }
        catch { }
    }

    private async Task PasteImageAsync(Windows.ApplicationModel.DataTransfer.DataPackageView content)
    {
        if (_chatId == null) return;
        try
        {
            var streamRef = await content.GetBitmapAsync();
            var stream = await streamRef.OpenReadAsync();

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            using var memStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, memStream);
            var pixels = await decoder.GetPixelDataAsync();
            encoder.SetPixelData(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode,
                decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY,
                pixels.DetachPixelData());
            await encoder.FlushAsync();

            memStream.Seek(0);
            var pngBytes = new byte[memStream.Size];
            var dataReader = new Windows.Storage.Streams.DataReader(memStream);
            await dataReader.LoadAsync((uint)memStream.Size);
            dataReader.ReadBytes(pngBytes);

            var fileName = $"paste_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var staged = new StagedAttachment
            {
                FileName = fileName,
                MimeType = "image/png",
                Bytes = pngBytes
            };

            try
            {
                using var thumbStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var tw = new Windows.Storage.Streams.DataWriter(thumbStream);
                tw.WriteBytes(pngBytes);
                await tw.StoreAsync();
                thumbStream.Seek(0);
                staged.Thumbnail = new BitmapImage();
                staged.Thumbnail.DecodePixelWidth = 80;
                await staged.Thumbnail.SetSourceAsync(thumbStream);
            }
            catch { }

            _stagedAttachments.Add(staged);
            RenderStagedAttachments();
            SendBtn.IsEnabled = true;
            MsgInput.Focus(FocusState.Programmatic);
        }
        catch { }
    }

    private void RenderStagedAttachments()
    {
        AttachmentPreviewStack.Children.Clear();
        if (_stagedAttachments.Count == 0)
        {
            AttachmentPreview.Visibility = Visibility.Collapsed;
            SendBtn.IsEnabled = !string.IsNullOrWhiteSpace(MsgInput.Text);
            return;
        }

        AttachmentPreview.Visibility = Visibility.Visible;
        foreach (var att in _stagedAttachments.ToList())
        {
            var card = new Grid { Width = 100, Height = 100 };

            if (att.IsImage && att.Thumbnail != null)
            {
                var img = new Image
                {
                    Source = att.Thumbnail,
                    Stretch = Stretch.UniformToFill,
                    Width = 100,
                    Height = 100
                };
                card.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = img });
            }
            else
            {
                var fileStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 4
                };
                fileStack.Children.Add(new FontIcon { Glyph = "\uE7C3", FontSize = 24, Foreground = B(Accent), HorizontalAlignment = HorizontalAlignment.Center });
                fileStack.Children.Add(Lbl(att.FileName.Length > 12 ? att.FileName[..12] + "..." : att.FileName, 10, Fg2, false, HorizontalAlignment.Center, maxLines: 2));
                card.Children.Add(new Border { Background = B(Surface), CornerRadius = new CornerRadius(8), Child = fileStack });
            }

            var removeBtn = new Button
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Background = B(Err),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -2, 0),
                Content = new FontIcon { Glyph = "\uE711", FontSize = 8, Foreground = B(Fg1) }
            };
            var capturedAtt = att;
            removeBtn.Click += (s, e) =>
            {
                _stagedAttachments.Remove(capturedAtt);
                RenderStagedAttachments();
            };
            card.Children.Add(removeBtn);

            AttachmentPreviewStack.Children.Add(card);
        }
    }

    private static List<Inline> ParseMarkdownInlines(string text, bool isOwn)
    {
        var inlines = new List<Inline>();
        var fgColor = isOwn ? SentFg : Fg1;

        var pattern = @"```([\s\S]*?)```|`([^`]+)`|\*\*(.+?)\*\*|\*(.+?)\*|~~(.+?)~~";
        var regex = new Regex(pattern);

        int lastIndex = 0;
        foreach (Match match in regex.Matches(text))
        {
            if (match.Index > lastIndex)
            {
                inlines.Add(new Run
                {
                    Text = text[lastIndex..match.Index],
                    Foreground = B(fgColor)
                });
            }

            if (match.Groups[1].Success)
            {
                inlines.Add(new Run
                {
                    Text = match.Groups[1].Value,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = B(isOwn ? SentFg : Accent)
                });
            }
            else if (match.Groups[2].Success)
            {
                inlines.Add(new Run
                {
                    Text = match.Groups[2].Value,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = B(isOwn ? SentFg : Accent)
                });
            }
            else if (match.Groups[3].Success)
            {
                inlines.Add(new Run
                {
                    Text = match.Groups[3].Value,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = B(fgColor)
                });
            }
            else if (match.Groups[4].Success)
            {
                inlines.Add(new Run
                {
                    Text = match.Groups[4].Value,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Foreground = B(fgColor)
                });
            }
            else if (match.Groups[5].Success)
            {
                inlines.Add(new Run
                {
                    Text = match.Groups[5].Value,
                    TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough,
                    Foreground = B(fgColor)
                });
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            inlines.Add(new Run
            {
                Text = text[lastIndex..],
                Foreground = B(fgColor)
            });
        }

        if (inlines.Count == 0)
        {
            inlines.Add(new Run { Text = text, Foreground = B(fgColor) });
        }

        return inlines;
    }

    private static void AnimateBubbleIn(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.InsertKeyFrame(0f, 0f);
        fadeAnim.InsertKeyFrame(1f, 1f);
        fadeAnim.Duration = TimeSpan.FromMilliseconds(200);
        visual.StartAnimation("Opacity", fadeAnim);
        var slideAnim = compositor.CreateVector3KeyFrameAnimation();
        slideAnim.InsertKeyFrame(0f, new Vector3(0f, 12f, 0f));
        slideAnim.InsertKeyFrame(1f, Vector3.Zero);
        slideAnim.Duration = TimeSpan.FromMilliseconds(200);
        visual.StartAnimation("Offset", slideAnim);
    }

    private void ShowScheduleFlyout()
    {
        if (_chatId == null || (string.IsNullOrWhiteSpace(MsgInput.Text) && _stagedAttachments.Count == 0)) return;

        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

        var opt15 = new MenuFlyoutItem { Text = "In 15 minutes" };
        opt15.Click += (s, e) => ScheduleWith(DateTimeOffset.Now.AddMinutes(15));
        flyout.Items.Add(opt15);

        var opt1h = new MenuFlyoutItem { Text = "In 1 hour" };
        opt1h.Click += (s, e) => ScheduleWith(DateTimeOffset.Now.AddHours(1));
        flyout.Items.Add(opt1h);

        var opt3h = new MenuFlyoutItem { Text = "In 3 hours" };
        opt3h.Click += (s, e) => ScheduleWith(DateTimeOffset.Now.AddHours(3));
        flyout.Items.Add(opt3h);

        var optMorn = new MenuFlyoutItem { Text = "Tomorrow morning (9 AM)" };
        optMorn.Click += (s, e) =>
        {
            var tomorrow = DateTimeOffset.Now.Date.AddDays(1).AddHours(9);
            ScheduleWith(new DateTimeOffset(tomorrow, DateTimeOffset.Now.Offset));
        };
        flyout.Items.Add(optMorn);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var optCustom = new MenuFlyoutItem { Text = "Custom..." };
        optCustom.Click += (s, e) => _ = ShowCustomScheduleAsync();
        flyout.Items.Add(optCustom);

        flyout.ShowAt(ScheduleBtn);
    }

    private void ScheduleWith(DateTimeOffset sendAt)
    {
        if (_chatId == null) return;
        var sm = new ScheduledMessage
        {
            ChatId = _chatId,
            Text = MsgInput.Text?.Trim() ?? "",
            SendAt = sendAt,
            Attachments = _stagedAttachments.Count > 0 ? _stagedAttachments.ToList() : []
        };
        _scheduledMessages.Add(sm);
        MsgInput.Text = "";
        _stagedAttachments.Clear();
        RenderStagedAttachments();
        SendBtn.IsEnabled = false;
        UpdateScheduledBar();
    }

    private async Task ShowCustomScheduleAsync()
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 280 };
        var dp = new DatePicker { Date = DateTimeOffset.Now };
        var tp = new TimePicker { Time = DateTimeOffset.Now.TimeOfDay, ClockIdentifier = "12HourClock" };
        panel.Children.Add(Lbl("Pick date & time", 14, Fg1, true));
        panel.Children.Add(dp);
        panel.Children.Add(tp);

        var dlg = new ContentDialog
        {
            Title = "Schedule message",
            Content = panel,
            PrimaryButtonText = "Schedule",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var date = dp.Date.Date;
            var time = tp.Time;
            var sendAt = new DateTimeOffset(date + time, DateTimeOffset.Now.Offset);
            if (sendAt > DateTimeOffset.Now)
                ScheduleWith(sendAt);
        }
    }

    private async Task ProcessScheduledMessagesAsync()
    {
        var due = _scheduledMessages.Where(m => m.SendAt <= DateTimeOffset.Now).ToList();
        foreach (var sm in due)
        {
            try
            {
                if (sm.Attachments is { Count: > 0 })
                {
                    foreach (var att in sm.Attachments)
                    {
                        var base64 = Convert.ToBase64String(att.Bytes);
                        var asset = await App.Api.UploadBase64Async(base64, att.FileName, att.MimeType);
                        if (asset?.UploadID != null)
                            await App.Api.SendMessageWithAttachmentAsync(sm.ChatId, sm.Text, asset.UploadID, att.MimeType, att.FileName);
                    }
                }
                else if (!string.IsNullOrEmpty(sm.Text))
                {
                    await App.Api.SendMessageAsync(sm.ChatId, sm.Text);
                }
            }
            catch { }
            _scheduledMessages.Remove(sm);
        }
        if (due.Count > 0)
            UpdateScheduledBar();
    }

    private void UpdateScheduledBar()
    {
        var count = _scheduledMessages.Count(m => m.ChatId == _chatId);
        if (count > 0)
        {
            ScheduledBarText.Text = count == 1 ? "1 scheduled message" : $"{count} scheduled messages";
            ScheduledBar.Visibility = Visibility.Visible;
        }
        else
        {
            ScheduledBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleGifPicker()
    {
        if (_gifFlyout != null)
        {
            _gifFlyout.Hide();
            _gifFlyout = null;
            return;
        }

        _gifFlyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top
        };
        _gifFlyout.Closed += (s, e) => _gifFlyout = null;

        if (!GifService.HasApiKey)
        {
            var setupPanel = new StackPanel { Width = 300, Spacing = 8, Padding = new Thickness(4) };
            setupPanel.Children.Add(Lbl("GIF Search Setup", 14, Fg1, true));
            setupPanel.Children.Add(Lbl("Enter a free KLIPY API key to enable GIF search.\nGet one at partner.klipy.com", 12, Fg2));
            var keyInput = new TextBox { PlaceholderText = "KLIPY API key" };
            setupPanel.Children.Add(keyInput);
            var saveBtn = new Button
            {
                Content = "Save",
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = B(Accent),
                Foreground = B(Fg1),
                Padding = new Thickness(16, 4, 16, 4),
                CornerRadius = new CornerRadius(4)
            };
            saveBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(keyInput.Text))
                {
                    App.Settings.GifApiKey = keyInput.Text.Trim();
                    _gifFlyout?.Hide();
                }
            };
            setupPanel.Children.Add(saveBtn);
            _gifFlyout.Content = setupPanel;
            _gifFlyout.ShowAt(GifBtn);
            return;
        }

        var container = new Grid { Width = 360, Height = 400 };
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var searchBox = new TextBox
        {
            PlaceholderText = "Search GIFs...",
            Margin = new Thickness(0, 0, 0, 8)
        };
        container.Children.Add(searchBox);

        var scroll = new ScrollViewer();
        var resultsPanel = new StackPanel { Spacing = 4 };
        scroll.Content = resultsPanel;
        Grid.SetRow(scroll, 1);
        container.Children.Add(scroll);

        _gifFlyout.Content = container;

        _ = LoadGifsAsync(resultsPanel, null);

        searchBox.TextChanged += (s, e) =>
        {
            _gifSearchCts?.Cancel();
            _gifSearchCts = new CancellationTokenSource();
            var token = _gifSearchCts.Token;
            _ = Task.Delay(300, token).ContinueWith(t =>
            {
                if (!token.IsCancellationRequested)
                    DispatcherQueue.TryEnqueue(() => _ = LoadGifsAsync(resultsPanel, searchBox.Text));
            }, TaskScheduler.Default);
        };

        _gifFlyout.ShowAt(GifBtn);
    }

    private async Task LoadGifsAsync(StackPanel panel, string? query)
    {
        panel.Children.Clear();
        panel.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0)
        });

        var gifs = string.IsNullOrWhiteSpace(query)
            ? await GifService.GetTrendingAsync()
            : await GifService.SearchAsync(query);

        panel.Children.Clear();

        if (gifs.Count == 0)
        {
            var msg = GifService.LastError ?? "No GIFs found";
            panel.Children.Add(Lbl(msg, 12, Fg3, false, HorizontalAlignment.Center, new Thickness(8, 20, 8, 0)));
            return;
        }

        Grid? currentRow = null;
        int col = 0;
        foreach (var gif in gifs)
        {
            if (col == 0)
            {
                currentRow = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 0, 0, 4) };
                currentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                currentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                panel.Children.Add(currentRow);
            }

            try
            {
                var bmp = new BitmapImage(new Uri(gif.PreviewUrl));
                bmp.DecodePixelWidth = 170;
                var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Height = 100 };
                var border = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Child = img,
                    Background = B(Surface)
                };
                var capturedGif = gif;
                border.PointerPressed += (s, e) =>
                {
                    if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                    _gifFlyout?.Hide();
                    _ = SendGifAsync(capturedGif);
                };
                Grid.SetColumn(border, col);
                currentRow!.Children.Add(border);
            }
            catch { }

            col = (col + 1) % 2;
        }
    }

    private async Task SendGifAsync(GifResult gif)
    {
        if (_chatId == null || string.IsNullOrEmpty(gif.FullUrl)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var gifBytes = await http.GetByteArrayAsync(gif.FullUrl);
            var base64 = Convert.ToBase64String(gifBytes);
            var fileName = $"gif_{gif.Id}.gif";

            var asset = await App.Api.UploadBase64Async(base64, fileName, "image/gif");
            if (asset?.UploadID != null)
            {
                var attachResult = await App.Api.SendMessageWithAttachmentAsync(
                    _chatId, null, asset.UploadID, "image/gif", fileName,
                    gif.Width, gif.Height);

                var optimistic = new BeeperMessage
                {
                    Text = null,
                    IsSender = true,
                    Timestamp = DateTimeOffset.Now.ToString("o"),
                    Type = "IMAGE",
                    SenderName = "You",
                    Attachments =
                    [
                        new BeeperAttachment { FileName = fileName, MimeType = "image/gif", SrcURL = gif.PreviewUrl, IsGif = true }
                    ]
                };
                if (attachResult?.PendingMessageID != null)
                {
                    optimistic.Id = attachResult.PendingMessageID;
                    _messageMap[optimistic.Id] = optimistic;
                }
                _allMessages.Add(optimistic);
                var bubble = MakeBubble(optimistic, false, false);
                MsgStack.Children.Add(bubble);
                AnimateBubbleIn(bubble);
                ScrollToBottom();
            }
        }
        catch { }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop to send";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            _ = HandleDropAsync(e.DataView);
        }
    }

    private async Task HandleDropAsync(Windows.ApplicationModel.DataTransfer.DataPackageView data)
    {
        if (_chatId == null) return;
        try
        {
            var items = await data.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is Windows.Storage.StorageFile file)
                {
                    var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
                    var bytes = new byte[buffer.Length];
                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                        reader.ReadBytes(bytes);

                    var mimeType = file.ContentType ?? "application/octet-stream";
                    var staged = new StagedAttachment
                    {
                        FileName = file.Name,
                        MimeType = mimeType,
                        Bytes = bytes,
                        LocalPath = file.Path
                    };

                    if (staged.IsImage)
                    {
                        try
                        {
                            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                            var writer = new Windows.Storage.Streams.DataWriter(stream);
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                            stream.Seek(0);
                            staged.Thumbnail = new BitmapImage();
                            staged.Thumbnail.DecodePixelWidth = 80;
                            await staged.Thumbnail.SetSourceAsync(stream);
                        }
                        catch { }
                    }

                    _stagedAttachments.Add(staged);
                }
            }
            RenderStagedAttachments();
            SendBtn.IsEnabled = true;
            MsgInput.Focus(FocusState.Programmatic);
        }
        catch { }
    }
}
