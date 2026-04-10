using System.Collections.Concurrent;
using System.Diagnostics;
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
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using BeeperWinUI.Services;
using static BeeperWinUI.Theme.T;

namespace BeeperWinUI.Views;

public sealed partial class ChatListControl : UserControl
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public event Action<BeeperChat>? ChatSelected;
    public event Action<string, string>? MessageReceived; // chatId, messageJson
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
    private string[] _censorLines = [];
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
        // Deterministic: same chat always gets same line
        var idx = Math.Abs(chat.Id.GetHashCode()) % _censorLines.Length;
        return _censorLines[idx];
    }

    private string CensorPreview(BeeperChat chat)
    {
        // Use a different line than the name by offsetting
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
            var ids = await Task.Run(() =>
            {
                var result = new HashSet<string>();
                string? cursor = null;
                bool hasMore = true;
                while (hasMore)
                {
                    using var http = new HttpClient();
                    http.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", App.Settings.AccessToken ?? "");
                    var url = "http://localhost:23373/v1/chats?inbox=low-priority&limit=50";
                    if (cursor != null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
                    var body = http.GetStringAsync(url).GetAwaiter().GetResult();
                    var response = JsonSerializer.Deserialize<ChatsResponse>(body, s_jsonOptions);
                    if (response?.Chats == null || response.Chats.Count == 0) break;
                    foreach (var c in response.Chats) result.Add(c.Id);
                    cursor = response.OldestCursor;
                    hasMore = response.HasMore && !string.IsNullOrEmpty(cursor);
                }
                return result;
            });

            // Safety check: if the API returned ALL chats, the inbox param is being ignored
            // — don't flag everything as low-priority or the "All" tab becomes empty
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
                var tokenStr = App.Settings.AccessToken ?? "";
                var results = await Task.Run(() => {
                    using var http = new HttpClient();
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenStr);
                    var body = http.GetStringAsync($"http://localhost:23373/v1/chats/search?q={Uri.EscapeDataString(query)}").GetAwaiter().GetResult();
                    var resp = JsonSerializer.Deserialize<ChatsResponse>(body, s_jsonOptions);
                    return resp?.Chats ?? [];
                });
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

            // Fetch low-priority chat IDs from API (local fields are never populated)
            await FetchLowPriorityIdsAsync();
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

        // Diagnostic: log Extra keys from first few chats to discover low-priority field names
        AppLog.Write($"[ChatDebug] Total chats loaded: {_allChats.Count}");
        foreach (var c in _allChats.Take(5))
        {
            AppLog.Write($"[ChatDebug] {c.Title}: AccountId={c.AccountId}, IsLowPriority={c.IsLowPriority}, Priority={c.Priority}, Tags=[{string.Join(",", c.Tags ?? [])}], SpaceId={c.SpaceId ?? "(null)"}, Type={c.Type}");
            if (c.Extra != null && c.Extra.Count > 0)
                AppLog.Write($"[ChatDebug]   Extra keys = {string.Join(", ", c.Extra.Keys)}");
        }

        // Log Telegram chats specifically for low-priority debugging
        var tgChats = _allChats.Where(c => ResolveNetwork(c.AccountId) is "telegram" or "line").Take(8).ToList();
        if (tgChats.Count > 0)
        {
            AppLog.Write($"[LowPri] Telegram/Line chats sample ({tgChats.Count}):");
            foreach (var c in tgChats)
            {
                AppLog.Write($"  {c.Title}: IsLowPriority={c.IsLowPriority}, Priority={c.Priority}, Tags=[{string.Join(",", c.Tags ?? [])}], Type={c.Type}");
                if (c.Extra != null && c.Extra.Count > 0)
                    AppLog.Write($"    Extra: {string.Join(", ", c.Extra.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
        }

        // Log Discord space IDs for server separation debugging
        var discordChats = _allChats.Where(c => ResolveNetwork(c.AccountId) == "discord").Take(10).ToList();
        if (discordChats.Count > 0)
        {
            AppLog.Write($"[Discord] Discord chats sample ({discordChats.Count}):");
            foreach (var c in discordChats)
                AppLog.Write($"  {c.Title}: SpaceId={c.SpaceId ?? "(null)"}, AccountId={c.AccountId}");
        }

        // Log account IDs found in chats
        var chatAcctIds = _allChats.Select(c => c.AccountId).Distinct().ToList();
        AppLog.Write($"[Accounts] Distinct account IDs in chats ({chatAcctIds.Count}): {string.Join(", ", chatAcctIds)}");

        ApplyFilters();
        StartRealTimeUpdates();
        ChatsLoaded?.Invoke();
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
        // Collect pinned chats at the start of the batch to render as a compact bubble grid
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
                unpinnedStart = batch.Count; // all are pinned

            if (pinnedChats.Count > 0)
            {
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

        // Avatar with optional unread badge overlay
        var avatarContainer = new Grid
        {
            Width = avatarSize, Height = avatarSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var bubbleTitle = _censorMode ? CensorName(chat) : (chat.Title ?? "?");
        var avatarEl = _censorMode
            ? Avatar(bubbleTitle, avatarSize, NetColor(chat.AccountId))
            : TryMakeAvatarElement(chat, avatarSize);
        avatarContainer.Children.Add(avatarEl);

        // Network dot
        var netDot = NetDot(chat.AccountId, 10);
        netDot.HorizontalAlignment = HorizontalAlignment.Right;
        netDot.VerticalAlignment = VerticalAlignment.Bottom;
        netDot.BorderBrush = B(Surface);
        netDot.BorderThickness = new Thickness(2);
        netDot.CornerRadius = new CornerRadius(7);
        netDot.Width = 12; netDot.Height = 12;
        avatarContainer.Children.Add(netDot);

        // Unread badge
        if (chat.UnreadCount > 0)
        {
            var badge = Badge(chat.UnreadCount);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, -2, -4, 0);
            avatarContainer.Children.Add(badge);
        }
        stack.Children.Add(avatarContainer);

        // Name label
        var name = Lbl(bubbleTitle, 11, Fg2, false, HorizontalAlignment.Center, maxLines: 1);
        name.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        name.MaxWidth = 80;
        stack.Children.Add(name);

        var border = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Background = B(isSelected ? Selected : Colors.Transparent),
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

        // Same context menu as regular rows
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
            if (child is Border b && b.Child is Grid)
                b.Background = Transparent;
        }

        if (hadUnread)
        {
            ApplyFilters();
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

    private FrameworkElement TryMakeAvatarElement(BeeperChat chat, double size = 40)
    {
        var participants = chat.Participants?.Items;
        if (participants == null || participants.Count == 0)
            return Avatar(chat.Title ?? "?", size, NetColor(chat.AccountId));

        var nonSelf = participants.Where(p => !p.IsSelf).ToList();

        if (chat.Type == "group" && nonSelf.Count >= 2)
        {
            var smallSize = size * 0.65;
            var grid = new Grid { Width = size, Height = size };
            var first = MakeSmallAvatar(nonSelf[0], smallSize);
            first.HorizontalAlignment = HorizontalAlignment.Left;
            first.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(first);
            var second = MakeSmallAvatar(nonSelf[1], smallSize);
            second.HorizontalAlignment = HorizontalAlignment.Right;
            second.VerticalAlignment = VerticalAlignment.Bottom;
            grid.Children.Add(second);
            return grid;
        }

        var person = nonSelf.FirstOrDefault() ?? participants.FirstOrDefault();
        if (person != null && !string.IsNullOrEmpty(person.ImgUrl))
            return TryMakeAvatarImage(person.ImgUrl, size, chat.Title ?? "?", chat.AccountId);

        return Avatar(chat.Title ?? "?", size, NetColor(chat.AccountId));
    }

    private static BitmapImage GetCachedBitmap(string url, int size)
    {
        return _avatarCache.GetOrAdd(url, u =>
        {
            var bmp = new BitmapImage(new Uri(u));
            bmp.DecodePixelWidth = size;
            bmp.DecodePixelHeight = size;
            return bmp;
        });
    }

    private static FrameworkElement MakeSmallAvatar(BeeperUser user, double size)
    {
        if (!string.IsNullOrEmpty(user.ImgUrl))
        {
            try
            {
                var bmp = GetCachedBitmap(user.ImgUrl, (int)size);
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
            var bmp = GetCachedBitmap(imgUrl, (int)size);
            var img = new Image { Source = bmp, Width = size, Height = size, Stretch = Stretch.UniformToFill };
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = img };
        }
        catch
        {
            return Avatar(fallbackName, size, NetColor(accountId));
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
                    var rawChat = chatEl.GetRawText();
                    AppLog.Write($"[WS] chat.upserted raw (len={rawChat.Length}): {rawChat[..Math.Min(500, rawChat.Length)]}");
                    var chat = JsonSerializer.Deserialize<BeeperChat>(rawChat, s_jsonOptions);
                    if (chat != null)
                    {
                        AppLog.Write($"[WS] chat.upserted: {chat.Title}, Preview={chat.Preview?.Text ?? "(null)"}");
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
                string? msgTimestamp = null;
                string? msgText = null;
                string? previewOverride = null;
                if (root.TryGetProperty("message", out var msgEl))
                {
                    // Log raw message JSON for debugging preview issues (truncated)
                    var rawMsg = msgEl.GetRawText();
                    AppLog.Write($"[WS] message.upserted raw (len={rawMsg.Length}): {rawMsg[..Math.Min(500, rawMsg.Length)]}");

                    if (msgEl.TryGetProperty("chatID", out var cidEl))
                        chatId = cidEl.GetString();
                    if (msgEl.TryGetProperty("timestamp", out var tsEl))
                        msgTimestamp = tsEl.GetString();
                    if (msgEl.TryGetProperty("text", out var txtEl))
                        msgText = txtEl.GetString();
                    // Fallback: some bridges use "body" instead of "text"
                    if (string.IsNullOrEmpty(msgText) && msgEl.TryGetProperty("body", out var bodyEl))
                        msgText = bodyEl.GetString();

                    // Extract attachment preview for non-text messages
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
                    var existing = _allChats.Find(c => c.Id == updated.Id);
                    if (existing != null && !string.IsNullOrEmpty(existing.LastActivity))
                    {
                        var cmp = string.Compare(existing.LastActivity, updated.LastActivity ?? "", StringComparison.Ordinal);
                        if (cmp > 0)
                        {
                            // WS already advanced timestamp beyond what API returned — keep WS data
                            updated.LastActivity = existing.LastActivity;
                            if (!string.IsNullOrEmpty(existing.Preview?.Text))
                                updated.Preview = existing.Preview;
                        }
                        // Single-chat GET doesn't return preview ("in list responses" only) —
                        // always preserve existing preview when API returns empty/null
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
            // Preserve existing preview if the incoming chat has no preview
            // (chat.upserted and single-chat GET often don't include preview text)
            var existing = _allChats[idx];
            if (string.IsNullOrEmpty(chat.Preview?.Text) && !string.IsNullOrEmpty(existing.Preview?.Text))
                chat.Preview = existing.Preview;
            _allChats[idx] = chat;
        }
        else
            _allChats.Insert(0, chat);
    }
}
