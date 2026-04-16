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
    private int _mentionSelectedIndex = -1;

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
        public int? Width { get; set; }
        public int? Height { get; set; }
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
            UpdateMentionPopup();
        };

        SendBtn.Click += (s, e) => _ = SendAsync();

        MsgInput.KeyDown += (s, e) =>
        {
            if (MentionPopup.Visibility == Visibility.Visible)
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    MentionPopup.Visibility = Visibility.Collapsed;
                    _mentionSelectedIndex = -1;
                    e.Handled = true;
                    return;
                }
                if (e.Key == Windows.System.VirtualKey.Down)
                {
                    _mentionSelectedIndex = Math.Min(_mentionSelectedIndex + 1, MentionList.Children.Count - 1);
                    HighlightMentionItem(_mentionSelectedIndex);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Windows.System.VirtualKey.Up)
                {
                    _mentionSelectedIndex = Math.Max(_mentionSelectedIndex - 1, 0);
                    HighlightMentionItem(_mentionSelectedIndex);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Tab)
                {
                    if (MentionList.Children.Count > 0)
                    {
                        var idx = _mentionSelectedIndex >= 0 ? _mentionSelectedIndex : 0;
                        SelectMentionAtIndex(idx);
                    }
                    e.Handled = true;
                    return;
                }
            }
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

        ImageOverlayClose.Click += (s, e) => CloseImageOverlay();
        ImageOverlay.Tapped += (s, e) =>
        {
            if (e.OriginalSource is Image) return;
            if (e.OriginalSource is Button) return;
            if (e.OriginalSource is FontIcon) return;
            CloseImageOverlay();
        };
        this.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && ImageOverlay.Visibility == Visibility.Visible)
            {
                CloseImageOverlay();
                e.Handled = true;
            }
        };
        ImageOverlayImg.DoubleTapped += OnImageOverlayDoubleTapped;

        ImageOverlayContainer.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            if (ImageOverlayScroll.ZoomFactor <= 1.05f) return;
            _imgDragActive = true;
            _imgDragLast = e.GetCurrentPoint(ImageOverlayScroll).Position;
            SetOverlayCursor(true);
            ((UIElement)s).CapturePointer(e.Pointer);
            e.Handled = true;
        };
        ImageOverlayContainer.PointerMoved += (s, e) =>
        {
            if (!_imgDragActive) return;
            var pos = e.GetCurrentPoint(ImageOverlayScroll).Position;
            var dx = pos.X - _imgDragLast.X;
            var dy = pos.Y - _imgDragLast.Y;
            _imgDragLast = pos;
            ImageOverlayScroll.ChangeView(
                ImageOverlayScroll.HorizontalOffset - dx,
                ImageOverlayScroll.VerticalOffset - dy,
                null, true);
            e.Handled = true;
        };
        ImageOverlayContainer.PointerReleased += (s, e) =>
        {
            if (!_imgDragActive) return;
            _imgDragActive = false;
            SetOverlayCursor(false);
            ((UIElement)s).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        };

        ImageOverlayScroll.ViewChanged += (s, e) =>
        {
            if (!_imgDragActive)
                SetOverlayCursor(false);
        };

        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += (s, e) => _ = ProcessScheduledMessagesAsync();
        _scheduleTimer.Start();
    }

    public void CloseOverlays()
    {
        CloseChatSearch();
        CancelEdit();
        CancelReply();
        CloseImageOverlay();
    }

    private string? _overlayImageUrl;
    private bool _imgDragActive;
    private Windows.Foundation.Point _imgDragLast;

    private void SetOverlayCursor(bool dragging)
    {
        ProtectedCursor = dragging
            ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll)
            : null;
    }

    private void ShowImageOverlay(BitmapImage source, string? imageUrl = null)
    {
        _overlayImageUrl = imageUrl;

        BitmapImage fullRes;
        if (source.UriSource != null)
        {
            fullRes = new BitmapImage(source.UriSource);
        }
        else
        {
            fullRes = source;
        }

        fullRes.ImageOpened += (s, e) =>
        {
            var bmpSrc = s as BitmapImage;
            if (bmpSrc == null) return;
            var imgW = bmpSrc.PixelWidth;
            var imgH = bmpSrc.PixelHeight;
            if (imgW <= 0 || imgH <= 0) return;

            var viewW = ImageOverlayScroll.ActualWidth - 48;
            var viewH = ImageOverlayScroll.ActualHeight - 48;
            if (viewW <= 0 || viewH <= 0) return;

            var scale = Math.Min(viewW / imgW, viewH / imgH);
            if (scale > 1) scale = 1;

            ImageOverlayContainer.Width = imgW * scale;
            ImageOverlayContainer.Height = imgH * scale;
            ImageOverlayScroll.ChangeView(null, null, 1f, true);
        };

        ImageOverlayImg.Source = fullRes;
        ImageOverlayScroll.ChangeView(null, null, 1f, true);
        ImageOverlay.Visibility = Visibility.Visible;
        ImageOverlay.Opacity = 0;

        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = "Copy image", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyItem.Click += (s, e) => _ = CopyOverlayImageAsync();
        menu.Items.Add(copyItem);
        var saveItem = new MenuFlyoutItem { Text = "Save image as...", Icon = new FontIcon { Glyph = "\uE74E" } };
        saveItem.Click += (s, e) => _ = SaveOverlayImageAsync();
        menu.Items.Add(saveItem);
        ImageOverlayImg.ContextFlyout = menu;

        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ImageOverlay);
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 0f);
        anim.InsertKeyFrame(1f, 1f);
        anim.Duration = TimeSpan.FromMilliseconds(200);
        visual.StartAnimation("Opacity", anim);
        ImageOverlay.Opacity = 1;
    }

    private void CloseImageOverlay()
    {
        if (ImageOverlay.Visibility == Visibility.Collapsed) return;

        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ImageOverlay);
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 1f);
        anim.InsertKeyFrame(1f, 0f);
        anim.Duration = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation("Opacity", anim);

        _ = Task.Delay(160).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ImageOverlay.Visibility = Visibility.Collapsed;
                ImageOverlayImg.Source = null;
                ImageOverlayContainer.Width = double.NaN;
                ImageOverlayContainer.Height = double.NaN;
                _overlayImageUrl = null;
                _imgDragActive = false;
                SetOverlayCursor(false);
            });
        });
    }

    private void OnImageOverlayDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var currentZoom = ImageOverlayScroll.ZoomFactor;
        if (currentZoom > 1.1f)
        {
            ImageOverlayScroll.ChangeView(null, null, 1f);
        }
        else
        {
            var tapPos = e.GetPosition(ImageOverlayContainer);
            var targetZoom = 3f;

            var viewW = ImageOverlayScroll.ViewportWidth;
            var viewH = ImageOverlayScroll.ViewportHeight;
            var scrollX = tapPos.X * targetZoom - viewW / 2;
            var scrollY = tapPos.Y * targetZoom - viewH / 2;

            ImageOverlayScroll.ChangeView(
                Math.Max(0, scrollX),
                Math.Max(0, scrollY),
                targetZoom);
        }
    }

    private async Task CopyOverlayImageAsync()
    {
        try
        {
            string? filePath = null;
            if (ImageOverlayImg.Source is BitmapImage bmp && bmp.UriSource != null)
                filePath = bmp.UriSource.LocalPath;

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[IMG] Copy failed: {ex.Message}");
        }
    }

    private async Task SaveOverlayImageAsync()
    {
        try
        {
            string? filePath = null;
            if (ImageOverlayImg.Source is BitmapImage bmp && bmp.UriSource != null)
                filePath = bmp.UriSource.LocalPath;

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.SuggestedFileName = "image";

            if (ext is ".png")
                picker.FileTypeChoices.Add("PNG Image", [".png"]);
            else if (ext is ".gif")
                picker.FileTypeChoices.Add("GIF Image", [".gif"]);
            else if (ext is ".webp")
                picker.FileTypeChoices.Add("WebP Image", [".webp"]);
            else
                picker.FileTypeChoices.Add("JPEG Image", [".jpg"]);

            var destFile = await picker.PickSaveFileAsync();
            if (destFile != null)
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                await Windows.Storage.FileIO.WriteBytesAsync(destFile, bytes);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[IMG] Save failed: {ex.Message}");
        }
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
                if (!string.IsNullOrEmpty(existing.Text) && existing.Text != msg.Text)
                    existing.IsEdited = true;
                if (msg.IsEdited) existing.IsEdited = true;
                existing.Text = msg.Text;
                existing.Reactions = msg.Reactions;
                existing.Attachments = msg.Attachments;
                existing.Type = msg.Type;
                UpdateBubbleForMessage(existing);
                return;
            }

            if (string.IsNullOrEmpty(msg.Text) && (msg.Attachments == null || msg.Attachments.Count == 0)
                && msg.Type == "TEXT") return;

            if (ShouldSkipMessage(msg)) return;

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

            var distFromBottom = MsgScroll.ScrollableHeight - MsgScroll.VerticalOffset;
            if (distFromBottom < 150 || msg.IsSender)
            {
                ScrollToBottom();
                if (!msg.IsSender)
                    _ = App.Api.MarkChatReadAsync(chatId, msg.Id);
            }
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
        var dedupMap = new Dictionary<string, BeeperMessage>();
        foreach (var m in response.Messages)
            dedupMap[m.Id] = m;
        _allMessages = dedupMap.Values.ToList();

        _messageMap.Clear();
        foreach (var m in _allMessages)
        {
            _messageMap[m.Id] = m;
            _replyCache[m.Id] = m;
        }

        RenderMessages(_allMessages);
        ScrollToBottom();
        UpdateScheduledBar();

        var latestId = _allMessages.LastOrDefault()?.Id;
        _ = App.Api.MarkChatReadAsync(chat.Id, latestId);
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

                var newMessages = response.Messages.Where(m => !_messageMap.ContainsKey(m.Id)).ToList();
                _allMessages.InsertRange(0, newMessages);
                foreach (var m in newMessages)
                {
                    _messageMap[m.Id] = m;
                    _replyCache[m.Id] = m;
                }

                var olderByTime = newMessages
                    .Where(m => m.Type != "REACTION")
                    .Where(m => !(string.IsNullOrEmpty(m.Text) && (m.Attachments == null || m.Attachments.Count == 0) && m.Type == "TEXT"))
                    .OrderBy(m => m.Timestamp)
                    .ToList();

                int insertIdx = 0;
                string? lastSender = null;
                string? lastTimestamp = null;

                foreach (var m in olderByTime)
                {
                    if (ShouldSkipMessage(m)) continue;

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

        var deduped = new Dictionary<string, BeeperMessage>();
        foreach (var m in messages)
            deduped[m.Id] = m;
        var msgs = deduped.Values.OrderBy(m => m.Timestamp).ToList();
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
                CornerRadius = new CornerRadius(14),
                Style = (Style)Resources["NoFlickerButton"]
            };
            loadBtn.Click += (s, e) => _ = LoadEarlierMessagesAsync();
            MsgStack.Children.Add(loadBtn);
        }

        foreach (var m in msgs)
        {
            if (m.Type == "REACTION") continue;

            if (string.IsNullOrEmpty(m.Text) && (m.Attachments == null || m.Attachments.Count == 0)
                && m.Type == "TEXT") continue;

            if (ShouldSkipMessage(m)) continue;

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
        var isAttFn = !string.IsNullOrEmpty(text) && (IsAttachmentFilename(text, m) || IsMediaUrl(text));
        if (!string.IsNullOrEmpty(text) && !isAttFn)
        {
            RenderTextWithQuotes(content, text, isOwn, m.Mentions);
        }
        else if ((m.Attachments == null || m.Attachments.Count == 0) && !isAttFn)
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

        var timeStr = MessageTime(m.Timestamp);
        if (m.IsEdited) timeStr += "  (edited)";
        content.Children.Add(Lbl(timeStr, 10, isOwn ? SentFg : Fg3,
            margin: new Thickness(0, 2, 0, 0)));

        if (m.Reactions?.Count > 0)
        {
            var rxRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
                Margin = new Thickness(0, 4, 0, 0) };
            var grouped = m.Reactions.GroupBy(r => r.DisplayEmoji);
            foreach (var g in grouped)
            {
                var emoji = g.Key;
                var rxBorder = new Border
                {
                    Background = B(Surface),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6, 2, 6, 2),
                    Child = Lbl($"{emoji} {g.Count()}", 11, Fg2)
                };
                var capturedMsg = m;
                var capturedEmoji = emoji;
                rxBorder.PointerPressed += (s, e) =>
                {
                    if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                    _ = AddReactionAsync(capturedMsg, capturedEmoji);
                };
                rxRow.Children.Add(rxBorder);
            }
            var addBtn = new Border
            {
                Background = B(Surface),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 2, 6, 2),
                Child = Lbl("+", 11, Fg3)
            };
            var capturedM = m;
            addBtn.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                ShowReactionPicker(capturedM, (Border)s);
            };
            rxRow.Children.Add(addBtn);
            content.Children.Add(rxRow);
        }

        var minChildren = 1 + (showSender ? 1 : 0) + (!string.IsNullOrEmpty(m.LinkedMessageId) ? 1 : 0);
        if (content.Children.Count <= minChildren)
        {
            return new Border { Visibility = Visibility.Collapsed, Height = 0 };
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

    private string? GetMentionQuery()
    {
        var text = MsgInput.Text;
        if (string.IsNullOrEmpty(text)) return null;
        var cursorPos = MsgInput.SelectionStart;
        if (cursorPos <= 0 || cursorPos > text.Length) return null;

        var searchStart = cursorPos - 1;
        for (int i = searchStart; i >= 0; i--)
        {
            if (text[i] == '@')
            {
                if (i == 0 || text[i - 1] == ' ' || text[i - 1] == '\n')
                    return text[(i + 1)..cursorPos];
                return null;
            }
            if (text[i] == ' ' || text[i] == '\n') return null;
        }
        return null;
    }

    private void UpdateMentionPopup()
    {
        var query = GetMentionQuery();
        if (query == null)
        {
            MentionPopup.Visibility = Visibility.Collapsed;
            return;
        }

        MentionList.Children.Clear();
        var queryLower = query.ToLowerInvariant();

        foreach (var p in _participantMap.Values)
        {
            if (p.IsSelf) continue;
            if (p.Id.Contains("bot:", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Id.Contains("bridge", StringComparison.OrdinalIgnoreCase)) continue;
            if ((p.FullName ?? "").Contains("bridge", StringComparison.OrdinalIgnoreCase)) continue;
            var name = p.FullName ?? p.Username ?? p.DisplayText ?? p.Id;
            if (queryLower.Length == 0 ||
                (name?.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.Username?.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ?? false) ||
                p.Id.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
            {
                AddMentionItem($"@{name}", p.Id, null, $"@{name}");
            }
        }

        if ("room".Contains(queryLower) || queryLower.Length == 0)
        {
            AddMentionItem("@room", "Notify everyone", "\uE716", "@room");
        }

        if (MentionList.Children.Count > 0)
        {
            MentionPopup.Visibility = Visibility.Visible;
            _mentionSelectedIndex = 0;
            HighlightMentionItem(0);
        }
        else
        {
            MentionPopup.Visibility = Visibility.Collapsed;
            _mentionSelectedIndex = -1;
        }
    }

    private void HighlightMentionItem(int index)
    {
        for (int i = 0; i < MentionList.Children.Count; i++)
        {
            if (MentionList.Children[i] is Border b)
            {
                b.Background = B(i == index ? Hover : Windows.UI.Color.FromArgb(0, 0, 0, 0));
                if (i == index)
                    b.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
        }
    }

    private void SelectMentionAtIndex(int index)
    {
        if (index < 0 || index >= MentionList.Children.Count) return;
        if (MentionList.Children[index] is Border b && b.Child is Grid row)
        {
            var textStack = (StackPanel)row.Children[row.Children.Count - 1];
            var label = (TextBlock)textStack.Children[0];
            InsertMention(label.Text);
        }
    }

    private void AddMentionItem(string displayName, string subtitle, string? iconGlyph, string insertText)
    {
        var row = new Grid { Padding = new Thickness(8, 6, 8, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (iconGlyph != null)
        {
            var icon = new FontIcon { Glyph = iconGlyph, FontSize = 14, Foreground = B(Accent),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);
        }

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(Lbl(displayName, 13, Fg1, true));
        if (!string.IsNullOrEmpty(subtitle) && subtitle != displayName)
            textStack.Children.Add(Lbl(subtitle, 10, Fg3));
        Grid.SetColumn(textStack, 1);
        row.Children.Add(textStack);

        var border = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(4),
            Background = B(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };

        border.PointerEntered += (s, _) => ((Border)s).Background = B(Hover);
        border.PointerExited += (s, _) => ((Border)s).Background = B(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        var capturedInsert = insertText;
        border.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            InsertMention(capturedInsert);
        };

        MentionList.Children.Add(border);
    }

    private void InsertMention(string mentionText)
    {
        var text = MsgInput.Text ?? "";
        var cursorPos = MsgInput.SelectionStart;

        for (int i = cursorPos - 1; i >= 0; i--)
        {
            if (text[i] == '@')
            {
                var before = text[..i];
                var after = cursorPos < text.Length ? text[cursorPos..] : "";
                MsgInput.Text = before + mentionText + " " + after;
                MsgInput.SelectionStart = (before + mentionText + " ").Length;
                break;
            }
        }

        MentionPopup.Visibility = Visibility.Collapsed;
        MsgInput.Focus(FocusState.Programmatic);
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

    private void ShowReactionPicker(BeeperMessage m, Border anchor)
    {
        var flyout = new MenuFlyout();
        var emojis = new[]
        {
            "\u2764\uFE0F", "\uD83D\uDC4D", "\uD83D\uDC4E", "\uD83D\uDE02", "\uD83D\uDE4F",
            "\uD83D\uDE0D", "\uD83D\uDE0E", "\uD83D\uDE2E", "\uD83D\uDE22", "\uD83E\uDD14",
            "\uD83E\uDD23", "\uD83D\uDE31", "\uD83D\uDE44",
            "\uD83D\uDC4F", "\uD83D\uDCAA", "\uD83D\uDE4C",
            "\uD83D\uDD25", "\uD83C\uDF89", "\u2705"
        };
        foreach (var emoji in emojis)
        {
            var item = new MenuFlyoutItem { Text = emoji };
            var capturedEmoji = emoji;
            item.Click += (s, e) => _ = AddReactionAsync(m, capturedEmoji);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
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
            replyText = StripSenderPrefix(replyMsg.Text, replyMsg) ?? "";
            if (string.IsNullOrEmpty(replyText))
            {
                if (replyMsg.Attachments?.Count > 0)
                {
                    var att = replyMsg.Attachments[0];
                    replyText = att.IsVoiceNote ? "Voice message"
                        : att.IsSticker ? "Sticker"
                        : (att.MimeType ?? "").StartsWith("image/") ? "Photo"
                        : (att.MimeType ?? "").StartsWith("video/") ? "Video"
                        : !string.IsNullOrEmpty(att.FileName) ? att.FileName
                        : "Attachment";
                }
                else
                {
                    replyText = replyMsg.Type switch
                    {
                        "IMAGE" => "Photo",
                        "VIDEO" => "Video",
                        "VOICE" or "AUDIO" => "Voice message",
                        "FILE" => "File",
                        "STICKER" => "Sticker",
                        _ => "Message"
                    };
                }
                replyText = $"\U0001F4CE {replyText}";
            }
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
        if (att.IsVoiceNote || (att.MimeType ?? "").StartsWith("audio/") || (att.Type ?? "") == "AUDIO")
        {
            return BuildAudioPlayer(att, isOwn);
        }

        if (att.IsSticker)
            return TryMakeImageElement(att, 128, 128, isOwn);

        var mimeType = att.MimeType ?? "";
        var attType = att.Type ?? "";
        var fileName = att.FileName ?? "";
        var fileExt = Path.GetExtension(fileName).ToLowerInvariant();
        var srcExt = "";
        try { srcExt = Path.GetExtension(new Uri(att.SrcURL ?? "http://x/x").AbsolutePath).ToLowerInvariant(); } catch { }

        bool isImageByExt = fileExt is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".svg" or ".heic" or ".avif"
                         || srcExt is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".svg" or ".heic" or ".avif";
        bool isGifByExt = fileExt is ".gif" || srcExt is ".gif";
        bool isImage = mimeType.StartsWith("image/") || attType == "IMAGE" || att.IsGif || isGifByExt || isImageByExt;

        if (isImage)
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
            var fn = (att.FileName ?? "").ToLowerInvariant();
            bool isGifVideo = att.IsGif
                || fn.Contains("tenor.com") || fn.Contains("giphy.com")
                || fn.Contains("/gif-") || fn.Contains("/gif/")
                || (fn.Contains("gif") && !fn.EndsWith(".mp4") && !fn.EndsWith(".mov"));

            if (isGifVideo && !string.IsNullOrEmpty(att.SrcURL))
                return BuildGifVideoElement(att, isOwn);

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
                },
                Style = (Style)Resources["NoFlickerButton"]
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

    private FrameworkElement BuildAudioPlayer(BeeperAttachment att, bool isOwn)
    {
        var srcUrl = att.SrcURL;
        var isVoice = att.IsVoiceNote;
        var duration = att.Duration ?? 0;

        var outerBorder = new Border
        {
            Background = B(isOwn ? AccentDark : Surface),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 2, 0, 2),
            MinWidth = 220
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var playIcon = new FontIcon { Glyph = "\uE768", FontSize = 14, Foreground = B(isOwn ? SentFg : Accent) };
        var playBtn = new Button
        {
            Width = 32, Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = B(isOwn ? SentBg : SurfaceAlt),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Content = playIcon,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Resources["NoFlickerButton"]
        };
        Grid.SetColumn(playBtn, 0);
        row.Children.Add(playBtn);

        var infoStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = 0,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 2)
        };
        infoStack.Children.Add(slider);

        var durationStr = isVoice ? "Voice message" : (att.FileName ?? "Audio");
        if (duration > 0)
        {
            var mins = (int)(duration / 60);
            var secs = (int)(duration % 60);
            durationStr = $"{mins}:{secs:D2}";
        }
        var timeLabel = Lbl(durationStr, 10, isOwn ? SentFg : Fg3);

        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        bottomRow.Children.Add(timeLabel);

        var volBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 3,
                Children = { new FontIcon { Glyph = "\uE767", FontSize = 10, Foreground = B(isOwn ? SentFg : Fg3) },
                             Lbl("50%", 9, isOwn ? SentFg : Fg3) }
            },
            Padding = new Thickness(5, 2, 5, 2),
            Background = B(isOwn ? SentBg : SurfaceAlt),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0, MinHeight = 0,
            Style = (Style)Resources["NoFlickerButton"]
        };
        var volSlider = new Slider
        {
            Minimum = 0, Maximum = 2, Value = 1,
            Width = 120, Height = 28,
            StepFrequency = 0.1,
            Margin = new Thickness(8, 4, 8, 4)
        };
        var volFlyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 4, Width = 140,
                Children = { Lbl("Volume", 11, Fg1, true), volSlider }
            }
        };
        volBtn.Flyout = volFlyout;
        bottomRow.Children.Add(volBtn);

        var speedLabel = Lbl("1x", 10, isOwn ? SentFg : Fg2);
        var speedBtn = new Button
        {
            Content = speedLabel,
            FontSize = 10,
            Padding = new Thickness(5, 2, 5, 2),
            Background = B(isOwn ? SentBg : SurfaceAlt),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0, MinHeight = 0,
            Style = (Style)Resources["NoFlickerButton"]
        };
        var speedSlider = new Slider
        {
            Minimum = 0.5, Maximum = 2.0, Value = 1.0,
            Width = 120, Height = 28,
            StepFrequency = 0.25,
            SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues,
            Margin = new Thickness(8, 4, 8, 4)
        };
        var speedValueLabel = Lbl("1x", 11, Fg2);
        speedValueLabel.HorizontalAlignment = HorizontalAlignment.Center;
        var speedFlyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 4, Width = 140,
                Children = { Lbl("Speed", 11, Fg1, true), speedSlider, speedValueLabel }
            }
        };
        speedBtn.Flyout = speedFlyout;
        speedBtn.Tag = 1.0;
        bottomRow.Children.Add(speedBtn);

        infoStack.Children.Add(bottomRow);

        Grid.SetColumn(infoStack, 1);
        row.Children.Add(infoStack);

        outerBorder.Child = row;

        if (string.IsNullOrEmpty(srcUrl))
            return outerBorder;

        Windows.Media.Playback.MediaPlayer? player = null;
        DispatcherTimer? progressTimer = null;
        bool isPlaying = false;
        bool updatingFromTimer = false;
        bool seekDragging = false;
        var capturedSrcUrl = srcUrl;
        double audioDuration = duration > 0 ? duration : 0;
        var playbackStopwatch = new System.Diagnostics.Stopwatch();
        double playbackOffset = 0;
        double playbackSpeed = 1.0;

        playBtn.Click += (s, e) =>
        {
            if (isPlaying && player != null)
            {
                player.Pause();
                isPlaying = false;
                playbackOffset += playbackStopwatch.Elapsed.TotalSeconds * playbackSpeed;
                playbackStopwatch.Stop();
                playIcon.Glyph = "\uE768"; // Play
                progressTimer?.Stop();
                return;
            }

            if (player != null)
            {
                player.Play();
                isPlaying = true;
                playbackStopwatch.Restart();
                playIcon.Glyph = "\uE769"; // Pause
                progressTimer?.Start();
                return;
            }

            playIcon.Glyph = "\uE916"; // Loading dots
            playBtn.IsEnabled = false;

            _ = LoadAndPlayAudioAsync(capturedSrcUrl, (loadedPlayer, estDur) =>
            {
                try
                {
                    player = loadedPlayer;
                    try { player.Volume = volSlider.Value; } catch { }
                    if (audioDuration <= 0 && estDur > 0) audioDuration = estDur;
                    isPlaying = true;
                    playIcon.Glyph = "\uE769"; // Pause
                    playBtn.IsEnabled = true;

                    updatingFromTimer = true;
                    try { slider.Maximum = audioDuration > 0 ? audioDuration : 100; slider.Value = 0; } catch { }
                    updatingFromTimer = false;

                    if (audioDuration > 0)
                    {
                        var tm = (int)(audioDuration / 60);
                        var tsec = (int)(audioDuration % 60);
                        timeLabel.Text = $"0:00 / {tm}:{tsec:D2}";
                    }

                    player.MediaOpened += (p, a) =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            try
                            {
                                var dur = player.PlaybackSession.NaturalDuration.TotalSeconds;
                                if (dur > 0)
                                {
                                    audioDuration = dur;
                                    updatingFromTimer = true;
                                    try { slider.Maximum = dur; } catch { }
                                    updatingFromTimer = false;
                                    var tm = (int)(dur / 60);
                                    var tsec = (int)(dur % 60);
                                    timeLabel.Text = $"0:00 / {tm}:{tsec:D2}";
                                }
                            }
                            catch { }
                        });
                    };

                    player.MediaFailed += (p, a) =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            isPlaying = false;
                            playIcon.Glyph = "\uE768";
                            progressTimer?.Stop();
                            playbackStopwatch.Stop();
                            timeLabel.Text = "Playback failed";
                        });
                    };

                    player.MediaEnded += (p, a) =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            isPlaying = false;
                            playIcon.Glyph = "\uE768";
                            progressTimer?.Stop();
                            playbackStopwatch.Stop();
                            playbackOffset = 0;
                            updatingFromTimer = true;
                            try { slider.Value = 0; } catch { }
                            updatingFromTimer = false;
                            if (audioDuration > 0)
                            {
                                var mins = (int)(audioDuration / 60);
                                var secs = (int)(audioDuration % 60);
                                timeLabel.Text = $"{mins}:{secs:D2}";
                            }
                            else
                                timeLabel.Text = isVoice ? "Voice message" : (att.FileName ?? "Audio");
                        });
                    };

                    progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    progressTimer.Tick += (t, te) =>
                    {
                        if (!isPlaying || player == null) return;
                        var pos = playbackOffset + playbackStopwatch.Elapsed.TotalSeconds * playbackSpeed;
                        if (audioDuration > 0 && pos > audioDuration) pos = audioDuration;

                        if (audioDuration > 0 && !seekDragging)
                        {
                            var capturedPos = pos;
                            updatingFromTimer = true;
                            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                            {
                                try
                                {
                                    slider.Maximum = audioDuration;
                                    slider.Value = capturedPos;
                                }
                                catch { }
                                updatingFromTimer = false;
                            });
                        }

                        var m = (int)(pos / 60);
                        var sec = (int)(pos % 60);
                        if (audioDuration > 0)
                        {
                            var tm = (int)(audioDuration / 60);
                            var tsec = (int)(audioDuration % 60);
                            timeLabel.Text = $"{m}:{sec:D2} / {tm}:{tsec:D2}";
                        }
                        else
                        {
                            timeLabel.Text = $"{m}:{sec:D2}";
                        }
                    };
                    progressTimer.Start();
                    playbackStopwatch.Restart();

                    player.Play();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[AUDIO] onReady crash: {ex.Message}");
                    playIcon.Glyph = "\uE768";
                    playBtn.IsEnabled = true;
                }
            }, () =>
            {
                playIcon.Glyph = "\uE768";
                playBtn.IsEnabled = true;
            });
        };

        slider.ValueChanged += (s, e) =>
        {
            if (updatingFromTimer || player == null || audioDuration <= 0) return;
            playbackOffset = e.NewValue;
            playbackStopwatch.Restart();
            try { player.PlaybackSession.Position = TimeSpan.FromSeconds(playbackOffset); }
            catch { }
        };

        var volBtnContent = (StackPanel)volBtn.Content;
        var volIcon = (FontIcon)volBtnContent.Children[0];
        var volPctLabel = (TextBlock)volBtnContent.Children[1];
        volSlider.ValueChanged += (s, e) =>
        {
            try { if (player != null) player.Volume = e.NewValue; } catch { }
            var pct = (int)(e.NewValue * 50);
            volPctLabel.Text = $"{pct}%";
            volIcon.Glyph = e.NewValue < 0.01 ? "\uE74F" : "\uE767";
        };

        speedSlider.ValueChanged += (s, e) =>
        {
            var spd = Math.Round(e.NewValue, 2);
            speedLabel.Text = $"{spd}x";
            speedValueLabel.Text = $"{spd}x";
            speedBtn.Tag = spd;
            if (isPlaying)
            {
                playbackOffset += playbackStopwatch.Elapsed.TotalSeconds * playbackSpeed;
                playbackStopwatch.Restart();
            }
            playbackSpeed = spd;
        };

        return outerBorder;
    }

    private async Task LoadAndPlayAudioAsync(string srcUrl, Action<Windows.Media.Playback.MediaPlayer, double> onReady, Action onFail)
    {
        try
        {
            var mxcUri = ExtractMxcUri(srcUrl);
            string? resolvedUrl = null;

            if (!string.IsNullOrEmpty(mxcUri))
            {
                try
                {
                    var result = await Task.Run(async () => await App.Api.DownloadAssetAsync(mxcUri));
                    resolvedUrl = result?.SrcURL;
                }
                catch { }
            }

            var downloadUrl = resolvedUrl ?? srcUrl;
            var bytes = await Task.Run(() =>
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                if (!string.IsNullOrEmpty(App.Settings.AccessToken))
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", App.Settings.AccessToken);
                return http.GetByteArrayAsync(downloadUrl).GetAwaiter().GetResult();
            });

            var tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Buzzr", "audio_cache");
            Directory.CreateDirectory(tempDir);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(srcUrl)))[..16];
            var ext = (bytes.Length > 4 && bytes[0] == 0x4F && bytes[1] == 0x67) ? ".ogg"
                : (bytes.Length > 4 && bytes[0] == 0x49 && bytes[1] == 0x44) ? ".mp3"
                : (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xFB) ? ".mp3"
                : ".m4a";
            var tempPath = Path.Combine(tempDir, hash + ext);
            await File.WriteAllBytesAsync(tempPath, bytes);

            AppLog.Write($"[AUDIO] Saved {bytes.Length} bytes to {tempPath}");

            var capturedEstDur = 0.0;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var player = new Windows.Media.Playback.MediaPlayer();
                    try { player.CommandManager.IsEnabled = false; } catch { }
                    player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(tempPath));
                    AppLog.Write($"[AUDIO] MediaPlayer created, estDur={capturedEstDur:F1}s");
                    onReady(player, capturedEstDur);
                    AppLog.Write($"[AUDIO] onReady completed successfully");
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[AUDIO] MediaPlayer creation failed: {ex.GetType().Name}: {ex.Message}");
                    onFail();
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Write($"[AUDIO] Load failed: {ex.Message}");
            DispatcherQueue.TryEnqueue(() => onFail());
        }
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
            MaxWidth = 300,
            Background = B(isOwn ? AccentDark : Surface),
            HorizontalAlignment = HorizontalAlignment.Left
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

        var urlToLoad = _resolvedUrlCache.TryGetValue(srcUrl, out var cachedUrl) ? cachedUrl
            : (srcUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
               || srcUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || srcUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ? srcUrl
            : null;

        if (urlToLoad != null)
        {
            if (urlToLoad.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
                || urlToLoad.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                _ = LoadImageViaApiAsync(container, urlToLoad, width, isOwn);
            }
            else
            {
                SetImageOnContainer(container, urlToLoad, width, isOwn);
            }
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
            var bmp = new BitmapImage();
            if (width > 0) bmp.DecodePixelWidth = width;
            var img = new Image
            {
                Source = bmp,
                MaxWidth = 300,
                MaxHeight = 300,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            bmp.ImageOpened += (s, e) =>
            {
                container.Background = null;
                container.MinWidth = 0;
                container.MinHeight = 0;
                ScrollToBottomIfNearEnd();
            };

            var capturedUrl = resolvedUrl;
            var capturedWidth = width;
            var capturedIsOwn = isOwn;
            bmp.ImageFailed += (s, e) =>
            {
                AppLog.Write($"[IMG] BitmapImage failed for {capturedUrl}: {e.ErrorMessage}, falling back to stream download");
                _ = LoadImageViaApiAsync(container, capturedUrl, capturedWidth, capturedIsOwn);
            };

            container.Child = img;
            bmp.UriSource = new Uri(resolvedUrl);

            var capturedBmp = bmp;
            container.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                ShowImageOverlay(capturedBmp);
            };
        }
        catch
        {
            container.Child = Lbl("[Image failed to load]", 12, isOwn ? SentFg : Fg2);
        }
    }

    private FrameworkElement BuildGifVideoElement(BeeperAttachment att, bool isOwn)
    {
        var vw = att.GetWidth();
        var vh = att.GetHeight();
        int maxW = 300, maxH = 250;
        if (vw > 0 && vh > 0)
        {
            var ratio = Math.Min((double)maxW / vw, (double)maxH / vh);
            if (ratio < 1) { vw = (int)(vw * ratio); vh = (int)(vh * ratio); }
        }
        else { vw = 200; vh = 150; }

        var container = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 2, 0, 2),
            Width = vw,
            Height = vh,
            Background = B(isOwn ? AccentDark : Surface)
        };

        var loadRing = new ProgressRing
        {
            IsActive = true, Width = 24, Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        container.Child = loadRing;

        var capturedSrcUrl = att.SrcURL!;
        var capturedW = vw;
        var capturedH = vh;

        _ = Task.Run(async () =>
        {
            try
            {
                var mxcUri = ExtractMxcUri(capturedSrcUrl);
                string? resolvedUrl = null;
                if (!string.IsNullOrEmpty(mxcUri))
                {
                    try
                    {
                        var result = await App.Api.DownloadAssetAsync(mxcUri);
                        resolvedUrl = result?.SrcURL;
                    }
                    catch { }
                }

                var downloadUrl = resolvedUrl ?? capturedSrcUrl;
                var bytes = await Task.Run(() =>
                {
                    using var http = new HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(30);
                    if (!string.IsNullOrEmpty(App.Settings.AccessToken))
                        http.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", App.Settings.AccessToken);
                    return http.GetByteArrayAsync(downloadUrl).GetAwaiter().GetResult();
                });

                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Buzzr", "img_cache");
                Directory.CreateDirectory(tempDir);
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(capturedSrcUrl)))[..16];
                var tempPath = Path.Combine(tempDir, hash + ".mp4");
                File.WriteAllBytes(tempPath, bytes);

                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var player = new Windows.Media.Playback.MediaPlayer();
                        player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(tempPath));
                        player.IsLoopingEnabled = true;
                        player.IsMuted = true;
                        player.AutoPlay = true;

                        var playerElement = new MediaPlayerElement
                        {
                            Width = capturedW,
                            Height = capturedH,
                            AreTransportControlsEnabled = false,
                            Stretch = Stretch.Uniform
                        };
                        playerElement.SetMediaPlayer(player);

                        container.Background = null;
                        container.Child = playerElement;
                        player.Play();
                    }
                    catch
                    {
                        container.Child = Lbl("[GIF failed to load]", 12, isOwn ? SentFg : Fg2);
                    }
                });
            }
            catch
            {
                DispatcherQueue.TryEnqueue(() =>
                    container.Child = Lbl("[GIF failed to load]", 12, isOwn ? SentFg : Fg2));
            }
        });

        return container;
    }

    private static string? ExtractMxcUri(string url)
    {
        try
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var mxcUri = query["uri"];
            if (!string.IsNullOrEmpty(mxcUri) && mxcUri.StartsWith("mxc://"))
                return mxcUri;
        }
        catch { }
        return null;
    }

    private async Task LoadImageViaApiAsync(Border container, string url, int width, bool isOwn)
    {
        try
        {
            var mxcUri = ExtractMxcUri(url);
            string? resolvedUrl = null;

            if (!string.IsNullOrEmpty(mxcUri))
            {
                AppLog.Write($"[IMG] Resolving mxc URI: {mxcUri}");
                try
                {
                    var result = await Task.Run(async () => await App.Api.DownloadAssetAsync(mxcUri));
                    resolvedUrl = result?.SrcURL;
                    AppLog.Write($"[IMG] DownloadAssetAsync returned: {resolvedUrl?.Substring(0, Math.Min(resolvedUrl?.Length ?? 0, 80))}");
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[IMG] DownloadAssetAsync failed: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(resolvedUrl)
                && !resolvedUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
                && !resolvedUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                DispatcherQueue.TryEnqueue(() => SetImageOnContainer(container, resolvedUrl, width, isOwn));
                return;
            }

            var downloadUrl = resolvedUrl ?? url;
            AppLog.Write($"[IMG] Downloading bytes from: {downloadUrl.Substring(0, Math.Min(downloadUrl.Length, 80))}");

            var bytes = await Task.Run(() =>
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                if (!string.IsNullOrEmpty(App.Settings.AccessToken))
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", App.Settings.AccessToken);
                return http.GetByteArrayAsync(downloadUrl).GetAwaiter().GetResult();
            });

            AppLog.Write($"[IMG] Downloaded {bytes.Length} bytes, first bytes: {(bytes.Length >= 4 ? $"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}" : "too short")}");

            bool isValidImage = bytes.Length > 8 && (
                (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) || // PNG
                (bytes[0] == 0xFF && bytes[1] == 0xD8) || // JPEG
                (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) || // GIF
                (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46) // RIFF (WebP)
            );

            if (!isValidImage)
            {
                AppLog.Write($"[IMG] Downloaded data is NOT a valid image format, trying API download with auth...");
                if (!string.IsNullOrEmpty(mxcUri))
                {
                    bytes = await Task.Run(() =>
                    {
                        using var http = new HttpClient();
                        http.Timeout = TimeSpan.FromSeconds(30);
                        if (!string.IsNullOrEmpty(App.Settings.AccessToken))
                            http.DefaultRequestHeaders.Authorization =
                                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", App.Settings.AccessToken);
                        var resp = http.GetAsync($"{BeeperApiService.ApiBaseUrl}/v1/assets/serve?uri={Uri.EscapeDataString(mxcUri)}").GetAwaiter().GetResult();
                        resp.EnsureSuccessStatusCode();
                        return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    });
                    AppLog.Write($"[IMG] Re-downloaded with auth: {bytes.Length} bytes, first: {(bytes.Length >= 4 ? $"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}" : "too short")}");
                    isValidImage = bytes.Length > 8 && (
                        (bytes[0] == 0x89 && bytes[1] == 0x50) || (bytes[0] == 0xFF && bytes[1] == 0xD8) ||
                        (bytes[0] == 0x47 && bytes[1] == 0x49) || (bytes[0] == 0x52 && bytes[1] == 0x49));
                }
            }

            if (!isValidImage)
            {
                AppLog.Write($"[IMG] Still not a valid image after auth retry");
                DispatcherQueue.TryEnqueue(() =>
                    container.Child = Lbl("[Image failed to load]", 12, isOwn ? SentFg : Fg2));
                return;
            }

            var capturedBytes = bytes;
            var capturedUrl = url;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var tempDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Buzzr", "img_cache");
                    Directory.CreateDirectory(tempDir);
                    var hash = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(capturedUrl)))[..16];
                    var ext = (capturedBytes[0] == 0x89) ? ".png"
                        : (capturedBytes[0] == 0xFF) ? ".jpg"
                        : (capturedBytes[0] == 0x47) ? ".gif"
                        : ".webp";
                    var tempPath = Path.Combine(tempDir, hash + ext);
                    File.WriteAllBytes(tempPath, capturedBytes);

                    var bmp = new BitmapImage(new Uri(tempPath));
                    if (width > 0) bmp.DecodePixelWidth = width;

                    var img = new Image
                    {
                        Source = bmp,
                        MaxWidth = 300,
                        MaxHeight = 300,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };

                    bmp.ImageOpened += (s, e) =>
                    {
                        container.Background = null;
                        container.MinWidth = 0;
                        container.MinHeight = 0;
                        ScrollToBottomIfNearEnd();
                    };
                    bmp.ImageFailed += (s, e) =>
                    {
                        AppLog.Write($"[IMG] File load failed: {e.ErrorMessage}");
                        container.Child = Lbl("[Image failed to load]", 12, isOwn ? SentFg : Fg2);
                    };

                    container.Child = img;

                    var clickBmp = bmp;
                    container.PointerPressed += (s, e) =>
                    {
                        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                        ShowImageOverlay(clickBmp);
                    };

                    AppLog.Write($"[IMG] Loaded from temp: {tempPath}");
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[IMG] Temp file error: {ex.Message}");
                    container.Child = Lbl("[Image failed to load]", 12, isOwn ? SentFg : Fg2);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Write($"[IMG] Error: {ex.Message}");
            DispatcherQueue.TryEnqueue(() =>
                container.Child = Lbl("[Image failed to load]", 12, isOwn ? SentFg : Fg2));
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

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindDescendant<T>(child);
            if (result != null) return result;
        }
        return null;
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

    private static bool IsAttachmentFilename(string text, BeeperMessage m)
    {
        if (m.Attachments == null || m.Attachments.Count == 0) return false;

        var trimmed = text.Trim();

        foreach (var att in m.Attachments)
        {
            if (!string.IsNullOrEmpty(att.FileName) &&
                !string.IsNullOrEmpty(System.IO.Path.GetExtension(att.FileName)) &&
                string.Equals(trimmed, att.FileName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (m.Type is "IMAGE" or "VIDEO" or "FILE" or "STICKER" or "VOICE" or "AUDIO")
        {
            var ext = Path.GetExtension(trimmed).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".heic" or ".avif"
                or ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm"
                or ".ogg" or ".mp3" or ".m4a" or ".wav" or ".aac" or ".flac" or ".opus" or ".wma"
                or ".pdf" or ".doc" or ".docx" or ".zip" or ".rar")
                return true;
        }

        if (m.Attachments.Count > 0 && !trimmed.Contains(' ') &&
            (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private bool ShouldSkipMessage(BeeperMessage m)
    {
        if ((m.Attachments == null || m.Attachments.Count == 0) && !string.IsNullOrEmpty(m.Text))
        {
            var raw = m.Text.Trim();
            if (IsMediaUrl(raw)) return true;
            var stripped = StripSenderPrefix(raw, m)?.Trim() ?? "";
            if (IsMediaUrl(stripped)) return true;
            if (ContainsMediaUrl(raw)) return true;
        }
        return false;
    }

    private static bool ContainsMediaUrl(string text)
    {
        var colonIdx = text.IndexOf(": ");
        if (colonIdx > 0 && colonIdx < 40)
        {
            var afterColon = text[(colonIdx + 2)..].Trim();
            if (IsMediaUrl(afterColon)) return true;
        }
        if (!text.Contains(' ') && !text.Contains('\n'))
        {
            if (text.Contains("tenor.com/", StringComparison.OrdinalIgnoreCase)
                || text.Contains("giphy.com/", StringComparison.OrdinalIgnoreCase)
                || text.Contains("cdn.discordapp.com/", StringComparison.OrdinalIgnoreCase)
                || text.Contains("media.discordapp.net/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsMediaUrl(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Contains(' ') || trimmed.Contains('\n')) return false;
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        return trimmed.Contains("tenor.com/", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("giphy.com/", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("cdn.discordapp.com/", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("media.discordapp.net/", StringComparison.OrdinalIgnoreCase);
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
                        int? sendWidth = att.Width ?? asset.Width;
                        int? sendHeight = att.Height ?? asset.Height;
                        var attachResult = await App.Api.SendMessageWithAttachmentAsync(
                            _chatId, null, asset.UploadID, att.MimeType, att.FileName,
                            sendWidth, sendHeight);

                        var resolvedSrc = att.LocalPath;
                        if (!string.IsNullOrEmpty(resolvedSrc) && !resolvedSrc.StartsWith("file://"))
                            resolvedSrc = "file:///" + resolvedSrc.Replace("\\", "/");
                        if (string.IsNullOrEmpty(resolvedSrc)) resolvedSrc = asset.SrcURL;

                        var msgType = att.IsImage ? "IMAGE" : "FILE";
                        var optimistic = new BeeperMessage
                        {
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

            if (hasText)
            {
                try
                {
                    var sendResult = await App.Api.SendMessageAsync(_chatId, text, replyId);
                    var optimistic = new BeeperMessage
                    {
                        Text = text,
                        IsSender = true,
                        Timestamp = DateTimeOffset.Now.ToString("o"),
                        Type = "TEXT",
                        SenderName = "You",
                        LinkedMessageId = replyId
                    };
                    if (sendResult?.PendingMessageID != null)
                    {
                        optimistic.Id = sendResult.PendingMessageID;
                        _messageMap[optimistic.Id] = optimistic;
                    }
                    _allMessages.Add(optimistic);
                    var bubble = MakeBubble(optimistic, false, false);
                    MsgStack.Children.Add(bubble);
                    AnimateBubbleIn(bubble);
                    ScrollToBottom();
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

                    try
                    {
                        var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                        staged.Width = (int)dec.PixelWidth;
                        staged.Height = (int)dec.PixelHeight;
                    }
                    catch { }

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
                Bytes = pngBytes,
                Width = (int)decoder.PixelWidth,
                Height = (int)decoder.PixelHeight
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

    private void RenderTextWithQuotes(StackPanel content, string text, bool isOwn, List<BeeperMention>? mentions = null)
    {
        var lines = text.Split('\n');
        var quoteLines = new List<string>();
        var normalLines = new List<string>();

        void FlushNormal()
        {
            if (normalLines.Count == 0) return;
            var joined = string.Join("\n", normalLines);
            var tb = new TextBlock
            {
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = B(isOwn ? SentFg : Fg1),
                FontFamily = new FontFamily("Segoe UI"),
                IsTextSelectionEnabled = true,
                SelectionHighlightColor = new SolidColorBrush(isOwn
                    ? Windows.UI.Color.FromArgb(100, SentFg.R, SentFg.G, SentFg.B)
                    : AccentSoft)
            };
            foreach (var inline in ParseMarkdownInlines(joined, isOwn, mentions))
                tb.Inlines.Add(inline);
            content.Children.Add(tb);
            normalLines.Clear();
        }

        void FlushQuote()
        {
            if (quoteLines.Count == 0) return;
            var joined = string.Join("\n", quoteLines);
            var tb = new TextBlock
            {
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = B(isOwn ? SentFg : Fg2),
                FontFamily = new FontFamily("Segoe UI"),
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                IsTextSelectionEnabled = true,
                SelectionHighlightColor = new SolidColorBrush(isOwn
                    ? Windows.UI.Color.FromArgb(100, SentFg.R, SentFg.G, SentFg.B)
                    : AccentSoft)
            };
            foreach (var inline in ParseMarkdownInlines(joined, isOwn, mentions))
                tb.Inlines.Add(inline);
            content.Children.Add(new Border
            {
                BorderBrush = B(Accent),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(8, 2, 0, 2),
                Margin = new Thickness(0, 2, 0, 2),
                Child = tb
            });
            quoteLines.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("> ") || line == ">")
            {
                FlushNormal();
                quoteLines.Add(line.Length > 2 ? line[2..] : "");
            }
            else
            {
                FlushQuote();
                normalLines.Add(line);
            }
        }
        FlushQuote();
        FlushNormal();
    }

    private List<Inline> ParseMarkdownInlines(string text, bool isOwn, List<BeeperMention>? mentions = null)
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

        var mentionDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool fromApi = mentions != null && mentions.Count > 0;
        if (fromApi)
        {
            foreach (var m in mentions)
            {
                if (!string.IsNullOrEmpty(m.DisplayName)) mentionDisplayNames.Add(m.DisplayName);
                if (!string.IsNullOrEmpty(m.UserId))
                {
                    var uid = m.UserId.TrimStart('@');
                    var colonIdx = uid.IndexOf(':');
                    if (colonIdx > 0) mentionDisplayNames.Add(uid[..colonIdx]);
                }
            }
        }

        if (!fromApi && text.Contains('@'))
        {
            foreach (var p in _participantMap.Values)
            {
                if (!string.IsNullOrEmpty(p.FullName)) mentionDisplayNames.Add(p.FullName);
                if (!string.IsNullOrEmpty(p.Username)) mentionDisplayNames.Add(p.Username);
                if (!string.IsNullOrEmpty(p.DisplayText)) mentionDisplayNames.Add(p.DisplayText);
            }
            mentionDisplayNames.Add("room");
        }

        if (mentionDisplayNames.Count == 0)
            return inlines;

        var mentionColor = isOwn ? SentFg : Accent;
        var processed = new List<Inline>();
        foreach (var inline in inlines)
        {
            if (inline is not Run run ||
                (run.FontFamily?.Source?.Contains("Cascadia", StringComparison.OrdinalIgnoreCase) == true) ||
                (run.FontFamily?.Source?.Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true))
            {
                processed.Add(inline);
                continue;
            }

            var runText = run.Text;
            int pos = 0;
            bool hadMention = false;

            foreach (var name in mentionDisplayNames)
            {
                if (string.IsNullOrEmpty(name)) continue;

                if (fromApi)
                {
                    var idx = runText.IndexOf(name, pos, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;

                    var matchStart = idx;
                    if (matchStart > 0 && runText[matchStart - 1] == '@')
                        matchStart--;

                    hadMention = true;
                    if (matchStart > pos)
                        processed.Add(new Run { Text = runText[pos..matchStart], Foreground = run.Foreground });

                    var matchText = runText[matchStart..(idx + name.Length)];
                    if (!matchText.StartsWith('@')) matchText = "@" + matchText;
                    var mentionRun = new Run
                    {
                        Text = matchText,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = B(mentionColor)
                    };
                    if (isOwn) mentionRun.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                    processed.Add(mentionRun);
                    pos = idx + name.Length;
                }
                else
                {
                    var searchFor = "@" + name;
                    var idx = runText.IndexOf(searchFor, pos, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;

                    hadMention = true;
                    if (idx > pos)
                        processed.Add(new Run { Text = runText[pos..idx], Foreground = run.Foreground });
                    var fallbackRun = new Run
                    {
                        Text = runText[idx..(idx + searchFor.Length)],
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = B(mentionColor)
                    };
                    if (isOwn) fallbackRun.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                    processed.Add(fallbackRun);
                    pos = idx + searchFor.Length;
                }
            }

            if (hadMention)
            {
                if (pos < runText.Length)
                    processed.Add(new Run { Text = runText[pos..], Foreground = run.Foreground });
            }
            else
            {
                processed.Add(inline);
            }
        }

        return processed;
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
                        {
                            int? sendWidth = att.Width ?? asset.Width;
                            int? sendHeight = att.Height ?? asset.Height;
                            await App.Api.SendMessageWithAttachmentAsync(
                                sm.ChatId, sm.Text, asset.UploadID, att.MimeType, att.FileName,
                                sendWidth, sendHeight);
                        }
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

                            try
                            {
                                var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                                staged.Width = (int)dec.PixelWidth;
                                staged.Height = (int)dec.PixelHeight;
                            }
                            catch { }

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
