using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Buzzr.Services;
using static Buzzr.Theme.T;

namespace Buzzr.Views;

public sealed partial class ChatListControl : UserControl
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public event Action<BeeperChat>? ChatSelected;
    public event Action<string, string>? MessageReceived;
    public event Action? ChatsLoaded;

    private string? _selectedChatId;
    private List<BeeperChat> _allChats = [];
    private Dictionary<string, BeeperAccount> _accountMap = [];
    private CancellationTokenSource? _searchCts;
    private string? _accountFilter;
    private string? _spaceFilter;
    private string _tabFilter = "all";
    private int _focusIndex = -1;
    private List<BeeperChat> _displayedChats = [];
    private List<BeeperChat> _filteredSorted = [];
    private const int RenderBatch = 50;
    private HashSet<string> _lowPriorityIds = [];
    private bool _lowPriLoaded;
    private static readonly ConcurrentDictionary<string, BitmapImage> _avatarCache = new();
    private CancellationTokenSource? _wsCts;
    private bool _censorMode;
    private readonly DateTime _startupTime = DateTime.UtcNow;

    private static readonly string _avatarCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Buzzr", "avatar_cache");
    private string[] _censorLines = [];
    private string? _draggedPinId;
    public bool IsCensored => _censorMode;

    public ChatListControl()
    {
        this.InitializeComponent();

        SearchBox.TextChanged += (s, e) => _ = OnSearchChangedAsync();
        SearchBox.KeyDown += OnSearchKeyDown;

        TabAll.Click += (s, e) => SetTab("all");
        TabUnread.Click += (s, e) => SetTab("unread");
        TabPinned.Click += (s, e) => SetTab("pinned");
        TabArchived.Click += (s, e) => SetTab("archived");
        TabLowPriority.Click += (s, e) => SetTab("lowpriority");

        NewChatBtn.Click += (s, e) => ShellPage.OpenNewChat?.Invoke();

        ChatScroll.ViewChanged += (s, e) =>
        {
            if (ChatScroll.ScrollableHeight > 0 &&
                ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 200)
                _ = LoadMoreAsync();
        };

        _ = LoadAllChatsAsync();
    }

    public void SetAccounts(List<BeeperAccount> accounts)
    {
        _accountMap.Clear();
        foreach (var a in accounts)
            _accountMap[a.AccountId] = a;
    }

    public void FilterByAccount(string? accountId, string? spaceId = null)
    {
        _accountFilter = accountId;
        _spaceFilter = spaceId;
        ApplyFilters();
    }

    public List<string> GetDistinctSpaceIds(string accountId) =>
        _allChats.Where(c => c.AccountId == accountId && !string.IsNullOrEmpty(c.SpaceId))
            .Select(c => c.SpaceId!).Distinct().ToList();

    public string? GetSpaceName(string accountId, string spaceId) =>
        _allChats.FirstOrDefault(c => c.AccountId == accountId && c.SpaceId == spaceId)?.Title;

    public List<string> GetAllChatAccountIds() =>
        _allChats.Select(c => c.AccountId).Distinct().ToList();

    public void FocusSearchBox() => SearchBox.Focus(FocusState.Programmatic);

    public void SetCensorMode(bool enabled)
    {
        _censorMode = enabled;
        if (enabled && _censorLines.Length == 0)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "censor.txt");
                if (File.Exists(path))
                    _censorLines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            }
            catch { }
            if (_censorLines.Length == 0)
                _censorLines = ["Censored"];
        }
        ApplyFilters();
    }

    private string CensorName(BeeperChat chat)
    {
        var idx = Math.Abs(chat.Id.GetHashCode()) % _censorLines.Length;
        return _censorLines[idx];
    }

    private string CensorPreview(BeeperChat chat)
    {
        var idx = (Math.Abs(chat.Id.GetHashCode()) + 7) % _censorLines.Length;
        return _censorLines[idx];
    }

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

        if (tab == "lowpriority" && !_lowPriLoaded)
        {
            _ = FetchLowPriorityAndApplyAsync();
            return;
        }
        ApplyFilters();
    }

    private async Task FetchLowPriorityAndApplyAsync()
    {
        await FetchLowPriorityIdsAsync();
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
        TabLowPriority.Background = B(_tabFilter == "lowpriority" ? Selected : Colors.Transparent);
        TabLowPriority.Foreground = B(_tabFilter == "lowpriority" ? Fg1 : Fg2);
    }

    private bool IsLowPriority(BeeperChat c) =>
        _lowPriorityIds.Contains(c.Id)
        || c.IsLowPriority || c.Priority is "low" or "deprioritized"
        || (c.Tags?.Contains("m.lowpriority") == true)
        || HasExtraFlag(c, "isLowPriority")
        || HasExtraFlag(c, "lowPriority");

    private static bool HasExtraFlag(BeeperChat c, string key) =>
        c.Extra != null && c.Extra.TryGetValue(key, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && v.GetString() is "true" or "low" or "deprioritized"));

    private async Task FetchLowPriorityIdsAsync()
    {
        try
        {
            var ids = new HashSet<string>();
            var resp = await App.Api.GetChatsAsync(limit: 50, inbox: "low-priority");
            if (resp?.Chats != null)
                foreach (var c in resp.Chats) ids.Add(c.Id);

            if (ids.Count > 0 && _allChats.Count > 0 && ids.Count >= _allChats.Count * 0.8)
            {
                AppLog.Write($"[LowPri] API returned {ids.Count}/{_allChats.Count} chats — inbox param likely unsupported, ignoring");
                _lowPriLoaded = true;
                return;
            }

            _lowPriorityIds = ids;
            _lowPriLoaded = true;
            AppLog.Write($"[LowPri] Fetched {ids.Count} low-priority chat IDs from API");
        }
        catch (Exception ex)
        {
            AppLog.Write($"[LowPri] Error fetching low-priority chats: {ex.Message}");
        }
    }

    private void ApplyFilters()
    {
        var filtered = _allChats.AsEnumerable();

        if (!string.IsNullOrEmpty(_accountFilter))
            filtered = filtered.Where(c => c.AccountId == _accountFilter);
        if (!string.IsNullOrEmpty(_spaceFilter))
            filtered = filtered.Where(c => c.SpaceId == _spaceFilter);

        filtered = _tabFilter switch
        {
            "unread" => filtered.Where(c => c.UnreadCount > 0),
            "pinned" => filtered.Where(c => c.IsPinned),
            "archived" => filtered.Where(c => c.IsArchived),
            "lowpriority" => filtered.Where(IsLowPriority),
            _ => filtered.Where(c => !c.IsArchived && !IsLowPriority(c))
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
                var results = await App.Api.SearchChatsAsync(query);
                if (!token.IsCancellationRequested)
                {
                    if (!string.IsNullOrEmpty(_accountFilter))
                        results = results.Where(c => c.AccountId == _accountFilter).ToList();
                    RenderChats(results);
                }
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
            var allChats = await App.Api.GetAllChatsAsync();
            _allChats = allChats;
            _sidecarReady = true;
            await FetchLowPriorityIdsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"[ChatList] LoadAllChats error: {ex.Message}");
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

        AppLog.Write($"[ChatList] Loaded {_allChats.Count} chats");

        ApplyFilters();
        StartRealTimeUpdates();
        ChatsLoaded?.Invoke();
    }

    private async Task RefreshFirstPageAsync()
    {
        try
        {
            var page = await App.Api.GetChatsAsync(limit: 50);
            if (page?.Chats == null || page.Chats.Count == 0) return;

            var freshById = page.Chats.ToDictionary(c => c.Id);
            for (int i = 0; i < _allChats.Count; i++)
            {
                if (freshById.TryGetValue(_allChats[i].Id, out var fresh))
                {
                    if (_allChats[i].IsPinned && !fresh.IsPinned)
                        fresh.IsPinned = true;
                    _allChats[i] = fresh;
                }
            }

            var existingIds = new HashSet<string>(_allChats.Select(c => c.Id));
            foreach (var c in page.Chats)
            {
                if (!existingIds.Contains(c.Id))
                    _allChats.Add(c);
            }

            DispatcherQueue.TryEnqueue(() => ApplyFilters());
        }
        catch { }
    }

    private CancellationTokenSource? _renderCts;

    private void RenderChats(List<BeeperChat> chats)
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        _ = RenderChatsAsync(chats, _renderCts.Token);
    }

    private async Task RenderChatsAsync(List<BeeperChat> chats, CancellationToken ct)
    {
        var sorted = await Task.Run(() => chats
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.LastActivity ?? "")
            .ToList());
        if (ct.IsCancellationRequested) return;

        _filteredSorted = sorted;
        _displayedChats = sorted.Take(RenderBatch).ToList();
        ListStack.Children.Clear();
        _focusIndex = -1;

        await RenderBatchAsync(_displayedChats, 0, ct);
    }

    private async Task RenderBatchAsync(List<BeeperChat> batch, int startIndex, CancellationToken ct)
    {
        var pinnedChats = new List<BeeperChat>();
        int unpinnedStart = 0;
        if (startIndex == 0)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch[i].IsPinned)
                    pinnedChats.Add(batch[i]);
                else { unpinnedStart = i; break; }
            }
            if (pinnedChats.Count > 0 && unpinnedStart == 0)
                unpinnedStart = batch.Count;

            if (pinnedChats.Count > 0)
            {
                var savedPinOrder = App.Settings.GetString("pin_order");
                if (!string.IsNullOrEmpty(savedPinOrder))
                {
                    var orderIds = savedPinOrder.Split(',').ToList();
                    pinnedChats = pinnedChats.OrderBy(c =>
                    {
                        var idx = orderIds.IndexOf(c.Id);
                        return idx >= 0 ? idx : 999;
                    }).ToList();
                }

                ListStack.Children.Add(MakePinnedGrid(pinnedChats));
                if (unpinnedStart < batch.Count)
                    ListStack.Children.Add(Divider());
            }
        }

        int start = startIndex == 0 ? unpinnedStart : 0;
        for (int i = start; i < batch.Count; i++)
        {
            if (ct.IsCancellationRequested) return;
            ListStack.Children.Add(MakeChatRow(batch[i]));
            if ((i - start) % 20 == 19) await Task.Yield();
        }
    }

    private FrameworkElement MakePinnedGrid(List<BeeperChat> pinned)
    {
        const int columns = 3;
        var grid = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        grid.ChildrenTransitions = [new Microsoft.UI.Xaml.Media.Animation.RepositionThemeTransition()];
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (pinned.Count + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < pinned.Count; i++)
        {
            var bubble = MakePinnedBubble(pinned[i]);
            Grid.SetColumn(bubble, i % columns);
            Grid.SetRow(bubble, i / columns);
            grid.Children.Add(bubble);
        }
        return grid;
    }

    private FrameworkElement MakePinnedBubble(BeeperChat chat)
    {
        var isSelected = chat.Id == _selectedChatId;
        const double avatarSize = 48;

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 4,
            Padding = new Thickness(4, 8, 4, 8),
        };

        var avatarContainer = new Grid
        {
            Width = avatarSize,
            Height = avatarSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var bubbleTitle = _censorMode ? CensorName(chat) : (chat.Title ?? "?");
        var avatarEl = _censorMode
            ? Avatar(bubbleTitle, avatarSize, NetColor(chat.AccountId))
            : TryMakeAvatarElement(chat, avatarSize);
        avatarContainer.Children.Add(avatarEl);

        var netDot = NetDot(chat.AccountId, 10);
        netDot.HorizontalAlignment = HorizontalAlignment.Right;
        netDot.VerticalAlignment = VerticalAlignment.Bottom;
        netDot.BorderBrush = B(Surface);
        netDot.BorderThickness = new Thickness(2);
        netDot.CornerRadius = new CornerRadius(7);
        netDot.Width = 12; netDot.Height = 12;
        avatarContainer.Children.Add(netDot);

        if (chat.UnreadCount > 0)
        {
            var badge = Badge(chat.UnreadCount);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, -2, -4, 0);
            avatarContainer.Children.Add(badge);
        }
        stack.Children.Add(avatarContainer);

        var name = Lbl(bubbleTitle, 11, Fg2, false, HorizontalAlignment.Center, maxLines: 1);
        name.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        name.MaxWidth = 80;
        stack.Children.Add(name);

        var border = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Background = B(isSelected ? Selected : Colors.Transparent),
            Tag = chat.Id,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        border.CanDrag = true;
        border.AllowDrop = true;

        border.DragStarting += (s, e) =>
        {
            _draggedPinId = chat.Id;
            e.Data.RequestedOperation = DataPackageOperation.Move;
            ((Border)s).Opacity = 0.4;
            ((Border)s).Background = B(Colors.Transparent);
        };

        border.DropCompleted += (s, e) =>
        {
            ((Border)s).Opacity = 1;
            _draggedPinId = null;
        };

        border.DragOver += (s, e) => { e.AcceptedOperation = DataPackageOperation.Move; };

        border.DragEnter += (s, e) =>
        {
            if (_draggedPinId == null || _draggedPinId == chat.Id) return;
            {
                var parentGrid = ((Border)s).Parent as Grid;
                if (parentGrid == null) return;

                var fromIdx = -1;
                var toIdx = -1;
                for (int idx = 0; idx < parentGrid.Children.Count; idx++)
                {
                    if (parentGrid.Children[idx] is Border b && b.Tag is string tid)
                    {
                        if (tid == _draggedPinId) fromIdx = idx;
                        if (tid == chat.Id) toIdx = idx;
                    }
                }

                if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
                {
                    var child = parentGrid.Children[fromIdx];
                    parentGrid.Children.RemoveAt(fromIdx);
                    parentGrid.Children.Insert(toIdx, child);

                    // update grid positions
                    const int cols = 3;
                    for (int idx = 0; idx < parentGrid.Children.Count; idx++)
                    {
                        Grid.SetColumn((FrameworkElement)parentGrid.Children[idx], idx % cols);
                        Grid.SetRow((FrameworkElement)parentGrid.Children[idx], idx / cols);
                    }

                    // save order from current visual order
                    var ids = parentGrid.Children
                        .OfType<Border>()
                        .Select(b => b.Tag as string)
                        .Where(id => id != null)
                        .ToList();
                    App.Settings.SetString("pin_order", string.Join(",", ids));
                }
            }
        };

        border.Drop += (s, e) =>
        {
            _draggedPinId = null;
        };

        border.PointerEntered += (s, _) =>
        {
            if (chat.Id != _selectedChatId) ((Border)s).Background = B(Hover);
        };
        border.PointerExited += (s, _) =>
        {
            ((Border)s).Background = B(chat.Id == _selectedChatId ? Selected : Colors.Transparent);
        };
        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            SelectChat(chat);
        };

        border.ContextFlyout = BuildChatContextMenu(chat);

        return border;
    }

    private bool _loadingMore;

    private async Task LoadMoreAsync()
    {
        if (_loadingMore || _displayedChats.Count >= _filteredSorted.Count) return;
        _loadingMore = true;
        try
        {
            var next = _filteredSorted.Skip(_displayedChats.Count).Take(RenderBatch).ToList();
            var startIndex = _displayedChats.Count;
            _displayedChats.AddRange(next);
            await RenderBatchAsync(next, startIndex, default);
        }
        finally { _loadingMore = false; }
    }

    private void SelectChat(BeeperChat chat)
    {
        _selectedChatId = chat.Id;
        bool hadUnread = chat.UnreadCount > 0;
        if (hadUnread)
            chat.UnreadCount = 0;

        foreach (var child in ListStack.Children)
        {
            if (child is Border b)
                b.Background = B(Colors.Transparent);
            else if (child is Grid pinGrid)
            {
                foreach (var pinChild in pinGrid.Children)
                {
                    if (pinChild is Border pb)
                        pb.Background = B(Colors.Transparent);
                }
            }
        }

        if (hadUnread)
        {
            ApplyFilters();
        }

        foreach (var child in ListStack.Children)
        {
            if (child is Border b && b.Tag is string tagId && tagId == chat.Id)
            {
                AnimateSelection(b, Selected);
                break;
            }
            else if (child is Grid pinGrid)
            {
                foreach (var pinChild in pinGrid.Children)
                {
                    if (pinChild is Border pb && pb.Tag is string pinTagId && pinTagId == chat.Id)
                    {
                        pb.Background = B(Selected);
                        break;
                    }
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

        var displayTitle = _censorMode ? CensorName(chat) : (chat.Title ?? "Unknown");

        var avatarGrid = new Grid { Width = 40, Height = 40 };
        var avatarElement = _censorMode
            ? Avatar(displayTitle, 40, NetColor(chat.AccountId))
            : TryMakeAvatarElement(chat);
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
        titleStack.Children.Add(Lbl(displayTitle, 13, Fg1, true, maxLines: 1));
        titleRow.Children.Add(titleStack);

        var time = Lbl(RelativeTime(chat.LastActivity), 11, Fg3);
        Grid.SetColumn(time, 1);
        titleRow.Children.Add(time);
        info.Children.Add(titleRow);

        var networkName = GetNetworkName(chat);
        info.Children.Add(Lbl(!string.IsNullOrEmpty(networkName) ? networkName : "Chat", 11, Fg3, margin: new Thickness(0, 1, 0, 0), maxLines: 1));

        var previewText = _censorMode ? CensorPreview(chat) : GetPreviewText(chat.Preview);
        if (previewText.Length > 60) previewText = previewText[..60] + "...";
        info.Children.Add(Lbl(!string.IsNullOrEmpty(previewText) ? previewText : " ", 12, Fg2, maxLines: 1, margin: new Thickness(0, 2, 0, 0)));

        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var rightStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        if (chat.UnreadCount > 0)
            rightStack.Children.Add(Badge(chat.UnreadCount));
        Grid.SetColumn(rightStack, 2);
        row.Children.Add(rightStack);

        var border = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(4),
            Background = B(isSelected ? Selected : Colors.Transparent),
            Opacity = chat.IsMuted ? 0.7 : 1.0,
            Tag = chat.Id
        };

        border.PointerEntered += (s, _) =>
        {
            if (chat.Id != _selectedChatId) AnimateSelection((Border)s, Hover);
        };
        border.PointerExited += (s, _) =>
        {
            ((Border)s).Background = B(chat.Id == _selectedChatId ? Selected : Colors.Transparent);
        };
        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            SelectChat(chat);
        };

        border.ContextFlyout = BuildChatContextMenu(chat);

        return border;
    }

    private MenuFlyout BuildChatContextMenu(BeeperChat chat)
    {
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

        return flyout;
    }

    public static FrameworkElement MakeAvatarPublic(BeeperChat chat, double size = 40) => new ChatListControl().TryMakeAvatarElement(chat, size);

    private FrameworkElement TryMakeAvatarElement(BeeperChat chat, double size = 40)
    {
        if (chat.Title is "Note to self" or "note to self")
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = B(Windows.UI.Color.FromArgb(255, 76, 194, 255)),
                Child = new FontIcon
                {
                    Glyph = "\uE70B",
                    FontSize = size * 0.45,
                    Foreground = B(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        var participants = chat.Participants?.Items;

        var contacts = participants?.Where(p =>
            !p.IsSelf &&
            !p.Id.Contains("bot:") &&
            p.Id != App.Settings.UserId
        ).ToList() ?? [];

        if (chat.Type != "group" || contacts.Count <= 1)
        {
            var person = contacts.FirstOrDefault()
                ?? participants?.FirstOrDefault(p => !p.Id.Contains("bot:") && p.Id != App.Settings.UserId);
            if (person != null && !string.IsNullOrEmpty(person.ImgUrl))
            {
                var avatar = TryMakeAvatarImage(person.ImgUrl, size, chat.Title ?? "?", chat.AccountId);
                if (avatar != null) return avatar;
            }

            if (!string.IsNullOrEmpty(chat.AvatarUrl))
            {
                var avatar = TryMakeAvatarImage(chat.AvatarUrl, size, chat.Title ?? "?", chat.AccountId);
                if (avatar != null) return avatar;
            }
        }

        bool hasRealGroupAvatar = false;
        if (chat.Type == "group" && !string.IsNullOrEmpty(chat.AvatarUrl))
        {
            hasRealGroupAvatar = !contacts.Any(c => c.ImgUrl == chat.AvatarUrl)
                && !(participants?.Any(p => p.ImgUrl == chat.AvatarUrl) ?? false);
        }

        if (chat.Type == "group" && hasRealGroupAvatar)
        {
            var avatar = TryMakeAvatarImage(chat.AvatarUrl!, size, chat.Title ?? "?", chat.AccountId);
            if (avatar != null) return avatar;
        }

        if (chat.Type == "group" && contacts.Count(c => !string.IsNullOrEmpty(c.ImgUrl)) >= 2)
        {
            var container = new Grid { Width = size, Height = size };

            container.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = B(Windows.UI.Color.FromArgb(255, 50, 50, 50))
            });

            var count = Math.Min(contacts.Count, 4);
            var smallSize = count <= 2 ? size * 0.45 : size * 0.38;
            var radius = size * 0.22;

            double[][] positions = count switch
            {
                2 => [[-0.7, 0], [0.7, 0]],
                3 => [[0, -0.75], [-0.7, 0.5], [0.7, 0.5]],
                _ => [[-0.55, -0.55], [0.55, -0.55], [-0.55, 0.55], [0.55, 0.55]],
            };

            for (int i = 0; i < count; i++)
            {
                var avatar = MakeSmallAvatar(contacts[i], smallSize);
                var cx = (size - smallSize) / 2 + positions[i][0] * radius;
                var cy = (size - smallSize) / 2 + positions[i][1] * radius;
                avatar.Margin = new Thickness(cx, cy, 0, 0);
                avatar.HorizontalAlignment = HorizontalAlignment.Left;
                avatar.VerticalAlignment = VerticalAlignment.Top;
                container.Children.Add(avatar);
            }

            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Child = container
            };
        }

        if (chat.Type == "group")
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = B(NetColor(chat.AccountId)),
                Child = new FontIcon
                {
                    Glyph = "\uE716",
                    FontSize = size * 0.45,
                    Foreground = B(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        return Avatar(chat.Title ?? "?", size, NetColor(chat.AccountId));
    }

    private static string? ExtractMxcUri(string url)
    {
        try
        {
            var uri = new Uri(url);
            var queryStr = uri.Query.TrimStart('?');
            foreach (var part in queryStr.Split('&'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "uri")
                {
                    var mxc = Uri.UnescapeDataString(kv[1]);
                    if (mxc.StartsWith("mxc://"))
                        return mxc;
                }
            }
        }
        catch { }
        return null;
    }

    private static string MxcToFileName(string mxcUri)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(mxcUri));
        return Convert.ToHexString(bytes).ToLowerInvariant() + ".img";
    }

    private static bool _sidecarReady = false;
    private DateTime _lastTagEvent = DateTime.MinValue;
    private bool _tagRefreshPending = false;

    private async Task DebouncedTagRefreshAsync()
    {
        while ((DateTime.UtcNow - _lastTagEvent).TotalSeconds < 3)
            await Task.Delay(1000);
        _tagRefreshPending = false;

        await RefreshFirstPageAsync();
    }

    public static BitmapImage? GetCachedBitmapPublic(string url, int size) => GetCachedBitmap(url, size);

    private static BitmapImage? GetCachedBitmap(string url, int size)
    {
        var pixelSize = size * 2;

        return _avatarCache.GetOrAdd(url, u =>
        {
            var mxc = ExtractMxcUri(u);
            if (mxc != null)
            {
                var fileName = MxcToFileName(mxc);
                var filePath = Path.Combine(_avatarCacheDir, fileName);
                if (File.Exists(filePath))
                {
                    var bmp = new BitmapImage();
                    bmp.DecodePixelWidth = pixelSize;
                    bmp.DecodePixelHeight = pixelSize;
                    bmp.UriSource = new Uri(filePath);
                    return bmp;
                }

                if (_sidecarReady)
                {
                    var bmp = new BitmapImage(new Uri(u));
                    bmp.DecodePixelWidth = pixelSize;
                    bmp.DecodePixelHeight = pixelSize;
                    _ = SaveAvatarToDiskAsync(u, filePath);
                    return bmp;
                }

                return null!;
            }

            if (!_sidecarReady) return null!;

            var fallback = new BitmapImage(new Uri(u));
            fallback.DecodePixelWidth = pixelSize;
            fallback.DecodePixelHeight = pixelSize;
            return fallback;
        });
    }

    private static async Task SaveAvatarToDiskAsync(string url, string filePath)
    {
        try
        {
            Directory.CreateDirectory(_avatarCacheDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var token = App.Settings.AccessToken;
            if (!string.IsNullOrEmpty(token))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var data = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(filePath, data);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[AvatarCache] Failed to save {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    public static (int count, long bytes) GetAvatarCacheStats()
    {
        try
        {
            if (!Directory.Exists(_avatarCacheDir)) return (0, 0);
            var files = Directory.GetFiles(_avatarCacheDir);
            long total = 0;
            foreach (var f in files)
                total += new FileInfo(f).Length;
            return (files.Length, total);
        }
        catch { return (0, 0); }
    }

    public static long GetChatCacheSize() => 0;

    public static void ClearAllCaches()
    {
        try
        {
            _avatarCache.Clear();
            if (Directory.Exists(_avatarCacheDir))
                Directory.Delete(_avatarCacheDir, recursive: true);
            AppLog.Write("[Cache] All caches cleared");
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Cache] Error clearing caches: {ex.Message}");
        }
    }

    private static FrameworkElement MakeSmallAvatar(BeeperUser user, double size)
    {
        if (!string.IsNullOrEmpty(user.ImgUrl))
        {
            try
            {
                var bmp = GetCachedBitmap(user.ImgUrl, (int)size);
                if (bmp == null) throw new Exception("no cache");
                var img = new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.UniformToFill };
                return new Border
                {
                    Width = size,
                    Height = size,
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

    private static Border? TryMakeAvatarImage(string imgUrl, double size, string fallbackName, string accountId)
    {
        try
        {
            var bmp = GetCachedBitmap(imgUrl, (int)size);
            if (bmp == null) return null;
            var img = new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.UniformToFill };
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = img };
        }
        catch
        {
            return null;
        }
    }

    private static string GetPreviewText(BeeperMessage? msg)
    {
        if (!string.IsNullOrEmpty(msg?.Text)) return msg.Text;
        if (msg?.Attachments is { Count: > 0 })
        {
            var a = msg.Attachments[0];
            if (a.IsVoiceNote) return "\ud83c\udfa4 Voice message";
            if (a.IsGif) return "GIF";
            if (a.IsSticker) return "Sticker";
            var mime = a.MimeType ?? "";
            if (mime.StartsWith("image/")) return "\ud83d\udcf7 Photo";
            if (mime.StartsWith("video/")) return "\ud83c\udfa5 Video";
            return "\ud83d\udcce " + (a.FileName ?? "File");
        }
        if (!string.IsNullOrEmpty(msg?.Type) && msg.Type != "TEXT")
            return $"[{msg.Type}]";
        return "";
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
                    await ws.ConnectAsync(new Uri($"{BeeperApiService.WsBaseUrl}/v1/ws"), cts.Token);

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
                    var rawChat = chatEl.GetRawText();
                    var chat = JsonSerializer.Deserialize<BeeperChat>(rawChat, s_jsonOptions);
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
            else if (evtType == "tags.updated")
            {
                _lastTagEvent = DateTime.UtcNow;
                if (!_tagRefreshPending)
                {
                    _tagRefreshPending = true;
                    _ = DebouncedTagRefreshAsync();
                }
            }
            else if (evtType == "message.upserted")
            {
                string? chatId = null;
                string? msgTimestamp = null;
                string? msgText = null;
                string? previewOverride = null;
                if (root.TryGetProperty("message", out var msgEl))
                {
                    if (msgEl.TryGetProperty("chatID", out var cidEl))
                        chatId = cidEl.GetString();
                    if (msgEl.TryGetProperty("timestamp", out var tsEl))
                        msgTimestamp = tsEl.GetString();
                    if (msgEl.TryGetProperty("text", out var txtEl))
                        msgText = txtEl.GetString();
                    if (string.IsNullOrEmpty(msgText) && msgEl.TryGetProperty("body", out var bodyEl))
                        msgText = bodyEl.GetString();

                    if (string.IsNullOrEmpty(msgText) && msgEl.TryGetProperty("attachments", out var attArr)
                        && attArr.ValueKind == JsonValueKind.Array && attArr.GetArrayLength() > 0)
                    {
                        var first = attArr[0];
                        var isVoice = first.TryGetProperty("isVoiceNote", out var vn) && vn.GetBoolean();
                        var isGif = first.TryGetProperty("isGif", out var gf) && gf.GetBoolean();
                        var isSticker = first.TryGetProperty("isSticker", out var st) && st.GetBoolean();
                        var mime = first.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "";

                        previewOverride = isVoice ? "\ud83c\udfa4 Voice message"
                            : isGif ? "GIF" : isSticker ? "Sticker"
                            : mime.StartsWith("image/") ? "\ud83d\udcf7 Photo"
                            : mime.StartsWith("video/") ? "\ud83c\udfa5 Video"
                            : "\ud83d\udcce File";
                    }
                }
                if (chatId == null && root.TryGetProperty("chatID", out var cidEl2))
                    chatId = cidEl2.GetString();

                AppLog.Write($"[WS] message.upserted: chatId={chatId}, text={msgText ?? "(null)"}, preview={previewOverride ?? "(null)"}, ts={msgTimestamp}");

                if (!string.IsNullOrEmpty(chatId))
                {
                    var capturedChatId = chatId;
                    var capturedTs = msgTimestamp;
                    var capturedText = msgText;
                    var capturedPreview = previewOverride;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        var existing = _allChats.Find(c => c.Id == capturedChatId);
                        if (existing != null)
                        {
                            if (!string.IsNullOrEmpty(capturedTs))
                                existing.LastActivity = capturedTs;
                            var textToSet = !string.IsNullOrEmpty(capturedText) ? capturedText : capturedPreview;
                            if (!string.IsNullOrEmpty(textToSet))
                                existing.Preview = new BeeperMessage { Text = textToSet };
                            AppLog.Write($"[WS] Updated chat '{existing.Title}': preview='{existing.Preview?.Text}', lastActivity={existing.LastActivity}");
                            ApplyFilters();
                        }
                    });
                    _ = RefreshChat(chatId);
                    var msgJson = root.TryGetProperty("message", out var mjEl) ? mjEl.GetRawText() : "";
                    DispatcherQueue.TryEnqueue(() => MessageReceived?.Invoke(capturedChatId, msgJson));

                    if (root.TryGetProperty("message", out var notifMsgEl))
                        TryShowToast(chatId, notifMsgEl);
                }
            }
        }
        catch { }
    }

    private void TryShowToast(string chatId, JsonElement msgEl)
    {
        try
        {
            if (!App.Settings.NotificationsEnabled) return;

            var isSender = msgEl.TryGetProperty("isSender", out var isSProp) && isSProp.GetBoolean();
            if (isSender) return;

            var isChatOpen = chatId == _selectedChatId && App.IsWindowFocused;
            if (isChatOpen) return;

            var sender = msgEl.TryGetProperty("senderName", out var snProp) ? snProp.GetString() ?? "Unknown" : "Unknown";
            var preview = msgEl.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? "" : "";
            if (preview.Length > 100) preview = preview[..100] + "...";
            if (string.IsNullOrEmpty(preview)) preview = "(attachment)";

            var builder = new AppNotificationBuilder()
                .AddText(sender)
                .AddText(preview);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch { }
    }

    public async Task RefreshChat(string chatId)
    {
        try
        {
            var updated = await App.Api.GetChatAsync(chatId);

            if (updated != null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var existing = _allChats.Find(c => c.Id == updated.Id);
                    if (existing != null && !string.IsNullOrEmpty(existing.LastActivity))
                    {
                        var cmp = string.Compare(existing.LastActivity, updated.LastActivity ?? "", StringComparison.Ordinal);
                        if (cmp > 0)
                        {
                            updated.LastActivity = existing.LastActivity;
                            if (!string.IsNullOrEmpty(existing.Preview?.Text))
                                updated.Preview = existing.Preview;
                        }
                        if (string.IsNullOrEmpty(updated.Preview?.Text) && !string.IsNullOrEmpty(existing.Preview?.Text))
                            updated.Preview = existing.Preview;
                        AppLog.Write($"[Refresh] {updated.Title}: existing.LA={existing.LastActivity}, updated.LA={updated.LastActivity}, preview='{updated.Preview?.Text}'");
                    }
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
        {
            var existing = _allChats[idx];
            if (string.IsNullOrEmpty(chat.Preview?.Text) && !string.IsNullOrEmpty(existing.Preview?.Text))
                chat.Preview = existing.Preview;
            _allChats[idx] = chat;
        }
        else
            _allChats.Insert(0, chat);
    }
}
