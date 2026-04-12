using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeperWinUI.Services;

public static class AppLog
{
    private static readonly string _path;
    static AppLog()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeeperWinUI");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "debug.log");
        try { File.WriteAllText(_path, $"=== BeeperWinUI Debug Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); } catch { }
    }
    public static void Write(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Debug.WriteLine(line);
        try { File.AppendAllText(_path, line + "\n"); } catch { }
    }
}

public class BeeperApiService : IDisposable
{
    private readonly JsonSerializerOptions _json;
    private static string BaseUrl = "http://localhost:23373";
    private string? _token;

    private static readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 20,
    };
    private static readonly HttpClient _http = new(_handler)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }

    public static string ApiBaseUrl => BaseUrl;
    public static string WsBaseUrl => BaseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
    public static void SetBaseUrl(string url) => BaseUrl = url.TrimEnd('/');

    public BeeperApiService()
    {
        _json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }

    public void SetToken(string token)
    {
        _token = token;
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);
    }

    private static void Log(string msg) => AppLog.Write($"[BeeperApi] {msg}");

    public async Task<BeeperInfo?> GetInfoAsync()
    {
        try
        {
            var info = await GetAsync<BeeperInfo>("/v1/info");
            IsConnected = info != null;
            return info;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        var info = await GetInfoAsync();
        return info != null;
    }

    public async Task<AuthLoginResponse?> AuthLoginAsync(string email)
    {
        return await PostAsync<AuthLoginResponse>("/v1/auth/login", new { email });
    }

    public async Task<AuthVerifyResponse?> AuthVerifyAsync(string email, string code)
    {
        return await PostAsync<AuthVerifyResponse>("/v1/auth/verify", new { email, code });
    }

    public async Task<AuthStatusResponse?> AuthStatusAsync()
    {
        return await GetAsync<AuthStatusResponse>("/v1/auth/status");
    }

    public async Task AuthLogoutAsync()
    {
        await PostAsync<object>("/v1/auth/logout", new { });
    }

    public async Task<RecoveryKeyResponse?> ImportRecoveryKeyAsync(string recoveryKey)
    {
        return await PostAsync<RecoveryKeyResponse>("/v1/auth/recovery", new { recoveryKey });
    }

    public async Task<VerificationStartResponse?> StartDeviceVerificationAsync()
    {
        return await PostAsync<VerificationStartResponse>("/v1/auth/verify-device", new { });
    }

    public async Task<VerificationStatusResponse?> GetVerificationStatusAsync()
    {
        return await GetAsync<VerificationStatusResponse>("/v1/auth/verify-device/status");
    }

    public async Task<VerificationConfirmResponse?> ConfirmVerificationAsync()
    {
        return await PostAsync<VerificationConfirmResponse>("/v1/auth/verify-device/confirm", new { });
    }

    public async Task<RecoveryKeyResponse?> ImportBeeperDesktopKeysAsync()
    {
        return await PostWithTimeoutAsync<RecoveryKeyResponse>("/v1/auth/import-beeper-keys", new { }, TimeSpan.FromMinutes(10));
    }

    public async Task<List<BeeperAccount>> GetAccountsAsync()
    {
        var result = await GetAsync<List<BeeperAccount>>("/v1/accounts");
        return result ?? [];
    }

    public async Task<SearchContactsOutput?> SearchContactsAsync(string accountId, string query)
    {
        var url = $"/v1/accounts/{Uri.EscapeDataString(accountId)}/contacts?q={Uri.EscapeDataString(query)}";
        return await GetAsync<SearchContactsOutput>(url);
    }

    public async Task<ListContactsOutput?> ListContactsAsync(string accountId, int limit = 100, string? cursor = null)
    {
        var url = $"/v1/accounts/{Uri.EscapeDataString(accountId)}/contacts/list?limit={limit}";
        if (cursor != null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        return await GetAsync<ListContactsOutput>(url);
    }

    public async Task<List<BeeperUser>> GetContactsAsync(string accountId, int limit = 100, string? cursor = null)
    {
        var result = await ListContactsAsync(accountId, limit, cursor);
        return result?.Items ?? [];
    }

    public async Task<ChatsResponse?> GetChatsAsync(int limit = 50, string? cursor = null,
        string? accountId = null, bool? isPinned = null, bool? isUnread = null,
        bool? isArchived = null, string? query = null, string? inbox = null)
    {
        var url = $"/v1/chats?limit={limit}";
        if (cursor != null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        if (accountId != null) url += $"&accountID={Uri.EscapeDataString(accountId)}";
        if (isPinned != null) url += $"&isPinned={isPinned.Value.ToString().ToLower()}";
        if (isUnread != null) url += $"&isUnread={isUnread.Value.ToString().ToLower()}";
        if (isArchived != null) url += $"&isArchived={isArchived.Value.ToString().ToLower()}";
        if (query != null) url += $"&q={Uri.EscapeDataString(query)}";
        if (inbox != null) url += $"&inbox={Uri.EscapeDataString(inbox)}";
        var result = await GetAsync<ChatsResponse>(url);
        return result;
    }

    public async Task<List<BeeperChat>> SearchChatsAsync(string query)
    {
        var result = await GetAsync<ChatsResponse>($"/v1/chats/search?q={Uri.EscapeDataString(query)}");
        return result?.Chats ?? [];
    }

    public async Task<BeeperChat?> GetChatAsync(string chatId)
    {
        return await GetAsync<BeeperChat>($"/v1/chats/{Uri.EscapeDataString(chatId)}");
    }

    public async Task<CreateChatOutput?> CreateChatAsync(CreateChatInput input)
    {
        return await PostAsync<CreateChatOutput>("/v1/chats", input);
    }

    public async Task<ArchiveChatOutput?> ArchiveChatAsync(string chatId)
    {
        return await PostAsync<ArchiveChatOutput>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/archive", new { });
    }

    public async Task UnarchiveChatAsync(string chatId)
    {
        await PostAsync<ArchiveChatOutput>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/archive", new { });
    }

    public async Task PinChatAsync(string chatId)
    {
        await PostAsync<object>($"/v1/chats/{Uri.EscapeDataString(chatId)}/pin", new { });
    }

    public async Task UnpinChatAsync(string chatId)
    {
        await PostAsync<object>($"/v1/chats/{Uri.EscapeDataString(chatId)}/unpin", new { });
    }

    public async Task MuteChatAsync(string chatId)
    {
        await PostAsync<object>($"/v1/chats/{Uri.EscapeDataString(chatId)}/mute", new { });
    }

    public async Task UnmuteChatAsync(string chatId)
    {
        await PostAsync<object>($"/v1/chats/{Uri.EscapeDataString(chatId)}/unmute", new { });
    }

    public async Task MarkChatReadAsync(string chatId)
    {
        await PostAsync<object>($"/v1/chats/{Uri.EscapeDataString(chatId)}/markread", new { });
    }

    public async Task<List<BeeperChat>> GetAllChatsAsync(string? accountId = null)
    {
        var all = new List<BeeperChat>();
        string? cursor = null;
        string? prevCursor = null;
        bool hasMore = true;
        int maxPages = 100;
        while (hasMore && maxPages-- > 0)
        {
            var page = await GetChatsAsync(limit: 50, cursor: cursor, accountId: accountId);
            if (page?.Chats == null || page.Chats.Count == 0) break;
            all.AddRange(page.Chats);
            hasMore = page.HasMore;
            cursor = page.OldestCursor;
            if (string.IsNullOrEmpty(cursor)) break;
            if (cursor == prevCursor) break;
            prevCursor = cursor;
        }
        return all;
    }

    public async Task<MessagesResponse?> GetMessagesAsync(string chatId, int limit = 25, string? cursor = null, string? direction = null)
    {
        var url = $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages?limit={limit}";
        if (cursor != null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        if (direction != null) url += $"&direction={direction}";
        return await GetAsync<MessagesResponse>(url);
    }

    public async Task<SendMessageOutput?> SendMessageAsync(string chatId, string text, string? replyToMessageId = null)
    {
        var body = new SendMessageInput { Text = text, ReplyToMessageID = replyToMessageId };
        return await PostAsync<SendMessageOutput>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages", body);
    }

    public async Task<SendMessageOutput?> SendMessageWithAttachmentAsync(
        string chatId, string? text, string uploadId, string mimeType, string fileName,
        int? width = null, int? height = null, double? duration = null, string? attachmentType = null)
    {
        var body = new SendMessageInput
        {
            Text = text,
            Attachment = new SendAttachmentInput
            {
                UploadID = uploadId,
                MimeType = mimeType,
                FileName = fileName,
                Size = (width != null && height != null)
                    ? new AttachmentSizeInput { Width = width.Value, Height = height.Value }
                    : null,
                Duration = duration,
                Type = attachmentType
            }
        };
        return await PostAsync<SendMessageOutput>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages", body);
    }

    public async Task<EditMessageOutput?> EditMessageAsync(string chatId, string messageId, string newText)
    {
        return await PutAsync<EditMessageOutput>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages/{Uri.EscapeDataString(messageId)}",
            new { text = newText });
    }

    public async Task<bool> DeleteMessageAsync(string chatId, string messageId)
    {
        var path = $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages/{Uri.EscapeDataString(messageId)}";
        Log($"[Delete] chatId={chatId}, messageId={messageId}");
        Log($"[Delete] path={path}");

        LastError = null;
        var r1 = await DeleteAsync<object>(path);
        Log($"[Delete] DELETE result={r1 != null}, LastError={LastError}");
        if (LastError == null) return true;

        LastError = null;
        var r2 = await PostAsync<object>(path + "/delete", new { });
        Log($"[Delete] POST /delete result={r2 != null}, LastError={LastError}");
        if (LastError == null) return true;

        LastError = null;
        var r3 = await PostAsync<object>(path + "/redact", new { });
        Log($"[Delete] POST /redact result={r3 != null}, LastError={LastError}");
        if (LastError == null) return true;

        LastError = null;
        var deletePath = $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages/delete";
        var r4 = await PostAsync<object>(deletePath, new { messageID = messageId });
        Log($"[Delete] POST messages/delete result={r4 != null}, LastError={LastError}");
        if (LastError == null) return true;

        Log($"[Delete] All attempts failed.");
        return false;
    }

    public async Task<AddReactionOutput?> AddReactionAsync(string chatId, string messageId, string reactionKey)
    {
        return await PostAsync<AddReactionOutput>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages/{Uri.EscapeDataString(messageId)}/reactions",
            new { reactionKey });
    }

    public async Task<RemoveReactionOutput?> RemoveReactionAsync(string chatId, string messageId)
    {
        return await DeleteAsync<RemoveReactionOutput>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages/{Uri.EscapeDataString(messageId)}/reactions");
    }

    public Task<UploadAssetOutput?> UploadAssetAsync(byte[] fileBytes, string fileName, string mimeType)
    {
        return Task.Run(() =>
        {
            Log($"POST /v1/assets/upload ({fileName}, {fileBytes.Length} bytes)");
            var http = _http;
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            form.Add(fileContent, "file", fileName);
            var response = http.PostAsync(BaseUrl + "/v1/assets/upload", form).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"POST /v1/assets/upload: {response.StatusCode} - {body}";
                Log(LastError);
                return null;
            }
            Log($"POST /v1/assets/upload -> {response.StatusCode}");
            return JsonSerializer.Deserialize<UploadAssetOutput>(body, _json);
        });
    }

    public async Task<UploadAssetOutput?> UploadBase64Async(string base64Content, string fileName, string mimeType)
    {
        return await PostAsync<UploadAssetOutput>("/v1/assets/upload/base64",
            new { content = base64Content, fileName, mimeType });
    }

    public async Task<DownloadAssetOutput?> DownloadAssetAsync(string url)
    {
        return await PostAsync<DownloadAssetOutput>("/v1/assets/download", new { url });
    }

    public static string GetAssetUrl(string assetUri)
    {
        return $"{BaseUrl}/v1/assets/serve?uri={Uri.EscapeDataString(assetUri)}";
    }

    public async Task<string?> UploadFileAsync(string filePath)
    {
        var bytes = await Task.Run(() => File.ReadAllBytes(filePath));
        var fileName = Path.GetFileName(filePath);
        var mimeType = GuessMimeType(fileName);
        var result = await UploadAssetAsync(bytes, fileName, mimeType);
        return result?.UploadID;
    }

    private static string GuessMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    public async Task<FocusOutput?> FocusAsync(FocusAppInput input)
    {
        return await PostAsync<FocusOutput>("/v1/focus", input);
    }

    public async Task<UnifiedSearchOutput?> SearchAsync(string query)
    {
        return await GetAsync<UnifiedSearchOutput>($"/v1/search?q={Uri.EscapeDataString(query)}");
    }

    public async Task<SearchMessagesOutput?> SearchMessagesAsync(string query, string? accountId = null)
    {
        var url = $"/v1/messages/search?q={Uri.EscapeDataString(query)}";
        if (accountId != null) url += $"&accountID={Uri.EscapeDataString(accountId)}";
        return await GetAsync<SearchMessagesOutput>(url);
    }

    public async Task SetReminderAsync(string chatId, SetReminderInput input)
    {
        await PostAsync<object>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/reminders", input);
    }

    public async Task RemoveReminderAsync(string chatId)
    {
        await DeleteAsync<object>(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/reminders");
    }

    private Task<T?> GetAsync<T>(string path) where T : class
    {
        return Task.Run(() =>
        {
            Log($"GET {path}");
            var http = _http;
            var response = http.GetAsync(BaseUrl + path).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"GET {path}: {response.StatusCode} - {body}";
                Log(LastError);
                return null;
            }
            Log($"GET {path} -> {response.StatusCode} ({body.Length} bytes)");
            return JsonSerializer.Deserialize<T>(body, _json);
        });
    }

    private Task<T?> PostAsync<T>(string path, object payload) where T : class
    {
        return Task.Run(() =>
        {
            Log($"POST {path}");
            var http = _http;
            var content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
            var response = http.PostAsync(BaseUrl + path, content).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"POST {path}: {response.StatusCode} - {body}";
                Log(LastError);
                return null;
            }
            Log($"POST {path} -> {response.StatusCode} ({body.Length} bytes)");
            if (string.IsNullOrWhiteSpace(body) || body == "{}") return null;
            return JsonSerializer.Deserialize<T>(body, _json);
        });
    }

    private Task<T?> PostWithTimeoutAsync<T>(string path, object payload, TimeSpan timeout) where T : class
    {
        return Task.Run(() =>
        {
            Log($"POST {path} (timeout={timeout.TotalSeconds}s)");
            using var cts = new CancellationTokenSource(timeout);
            using var longHttp = new HttpClient { Timeout = timeout };
            longHttp.DefaultRequestHeaders.Authorization = _http.DefaultRequestHeaders.Authorization;
            var content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
            var response = longHttp.PostAsync(BaseUrl + path, content, cts.Token).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"POST {path}: {response.StatusCode} - {body}";
                Log(LastError);
                return null;
            }
            Log($"POST {path} -> {response.StatusCode} ({body.Length} bytes)");
            if (string.IsNullOrWhiteSpace(body) || body == "{}") return null;
            return JsonSerializer.Deserialize<T>(body, _json);
        });
    }

    private Task<T?> PutAsync<T>(string path, object payload) where T : class
    {
        return Task.Run(() =>
        {
            Log($"PUT {path}");
            var http = _http;
            var content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
            var response = http.PutAsync(BaseUrl + path, content).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"PUT {path}: {response.StatusCode} - {body}";
                Log(LastError);
                return null;
            }
            Log($"PUT {path} -> {response.StatusCode} ({body.Length} bytes)");
            if (string.IsNullOrWhiteSpace(body) || body == "{}") return null;
            return JsonSerializer.Deserialize<T>(body, _json);
        });
    }

    private Task<T?> DeleteAsync<T>(string path) where T : class
    {
        return Task.Run(() =>
        {
            Log($"DELETE {path}");
            var http = _http;
            var response = http.DeleteAsync(BaseUrl + path).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"DELETE {path}: {response.StatusCode} - {body}";
                Log(LastError);
                return null;
            }
            Log($"DELETE {path} -> {response.StatusCode} ({body.Length} bytes)");
            if (string.IsNullOrWhiteSpace(body) || body == "{}") return null;
            return JsonSerializer.Deserialize<T>(body, _json);
        });
    }

    public void Dispose() { }
}

public class BeeperInfo
{
    [JsonPropertyName("app")] public BeeperAppInfo? App { get; set; }
    [JsonPropertyName("platform")] public BeeperPlatformInfo? Platform { get; set; }
    [JsonPropertyName("server")] public BeeperServerInfo? Server { get; set; }
    [JsonPropertyName("endpoints")] public BeeperEndpoints? Endpoints { get; set; }
    public string? Version => App?.Version;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperAppInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperPlatformInfo
{
    [JsonPropertyName("os")] public string? Os { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperServerInfo
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperEndpoints
{
    [JsonPropertyName("oauth")] public JsonElement? OAuth { get; set; }
    [JsonPropertyName("spec")] public string? Spec { get; set; }
    [JsonPropertyName("mcp")] public string? Mcp { get; set; }
    [JsonPropertyName("ws_events")] public string? WsEvents { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperAccount
{
    [JsonPropertyName("accountID")] public string AccountId { get; set; } = "";
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("user")] public BeeperUser? User { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("phoneNumber")] public string? PhoneNumber { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("fullName")] public string? FullName { get; set; }
    [JsonPropertyName("imgURL")] public string? ImgUrl { get; set; }
    [JsonPropertyName("cannotMessage")] public bool CannotMessage { get; set; }
    [JsonPropertyName("isSelf")] public bool IsSelf { get; set; }
    [JsonPropertyName("displayText")] public string? DisplayText { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class SearchContactsOutput
{
    [JsonPropertyName("items")] public List<BeeperUser>? Items { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class ListContactsOutput
{
    [JsonPropertyName("items")] public List<BeeperUser>? Items { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
    [JsonPropertyName("oldestCursor")] public string? OldestCursor { get; set; }
    [JsonPropertyName("newestCursor")] public string? NewestCursor { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class ChatsResponse
{
    [JsonPropertyName("items")] public List<BeeperChat>? Chats { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
    [JsonPropertyName("oldestCursor")] public string? OldestCursor { get; set; }
    [JsonPropertyName("newestCursor")] public string? NewestCursor { get; set; }
    public string? Cursor => OldestCursor;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperChat
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("localChatID")] public string? LocalChatId { get; set; }
    [JsonPropertyName("accountID")] public string AccountId { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("avatarURL")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("participants")] public PaginatedUsers? Participants { get; set; }
    [JsonPropertyName("lastActivity")] public string? LastActivity { get; set; }
    [JsonPropertyName("unreadCount")] public int UnreadCount { get; set; }
    [JsonPropertyName("isArchived")] public bool IsArchived { get; set; }
    [JsonPropertyName("isMuted")] public bool IsMuted { get; set; }
    [JsonPropertyName("isPinned")] public bool IsPinned { get; set; }
    [JsonPropertyName("preview")] public BeeperMessage? Preview { get; set; }
    [JsonPropertyName("isLowPriority")] public bool IsLowPriority { get; set; }
    [JsonPropertyName("priority")] public string? Priority { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("spaceID")] public string? SpaceId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class PaginatedUsers
{
    [JsonPropertyName("items")] public List<BeeperUser>? Items { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class CreateChatInput
{
    [JsonPropertyName("accountID")] public string? AccountID { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("participantIDs")] public List<string>? ParticipantIDs { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("user")] public CreateChatUser? User { get; set; }
    [JsonPropertyName("messageText")] public string? MessageText { get; set; }
    [JsonPropertyName("allowInvite")] public bool? AllowInvite { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class CreateChatUser
{
    [JsonPropertyName("identifier")] public string? Identifier { get; set; }
    [JsonPropertyName("identifierType")] public string? IdentifierType { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class CreateChatOutput
{
    [JsonPropertyName("chatID")] public string? ChatID { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class ArchiveChatOutput
{
    [JsonPropertyName("isArchived")] public bool IsArchived { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class MessagesResponse
{
    [JsonPropertyName("items")] public List<BeeperMessage>? Messages { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
    [JsonPropertyName("oldestCursor")] public string? OldestCursor { get; set; }
    [JsonPropertyName("newestCursor")] public string? NewestCursor { get; set; }
    public string? Cursor => OldestCursor;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("chatID")] public string ChatId { get; set; } = "";
    [JsonPropertyName("accountID")] public string AccountId { get; set; } = "";
    [JsonPropertyName("senderID")] public string SenderId { get; set; } = "";
    [JsonPropertyName("senderName")] public string? SenderName { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("sortKey")] public string? SortKey { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("isSender")] public bool IsSender { get; set; }
    [JsonPropertyName("isUnread")] public bool IsUnread { get; set; }
    [JsonPropertyName("linkedMessageID")] public string? LinkedMessageId { get; set; }
    [JsonPropertyName("attachments")] public List<BeeperAttachment>? Attachments { get; set; }
    [JsonPropertyName("reactions")] public List<BeeperReaction>? Reactions { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class BeeperAttachment
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("srcURL")] public string? SrcURL { get; set; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("fileSize")] public long? FileSize { get; set; }
    [JsonPropertyName("isGif")] public bool IsGif { get; set; }
    [JsonPropertyName("isSticker")] public bool IsSticker { get; set; }
    [JsonPropertyName("isVoiceNote")] public bool IsVoiceNote { get; set; }
    [JsonPropertyName("size")] public JsonElement? Size { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public int GetWidth()
    {
        if (Size?.ValueKind == JsonValueKind.Object && Size.Value.TryGetProperty("width", out var w))
            return w.TryGetInt32(out var wv) ? wv : 0;
        return 0;
    }

    public int GetHeight()
    {
        if (Size?.ValueKind == JsonValueKind.Object && Size.Value.TryGetProperty("height", out var h))
            return h.TryGetInt32(out var hv) ? hv : 0;
        return 0;
    }
}

public class BeeperReaction
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("reactionKey")] public string? ReactionKey { get; set; }
    [JsonPropertyName("imgURL")] public string? ImgURL { get; set; }
    [JsonPropertyName("participantID")] public string? ParticipantID { get; set; }
    [JsonPropertyName("emoji")] public JsonElement? Emoji { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string DisplayEmoji => ReactionKey ?? (Emoji?.ValueKind == JsonValueKind.String ? Emoji.Value.GetString() : null) ?? "?";
}

public class SendMessageInput
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("replyToMessageID")] public string? ReplyToMessageID { get; set; }
    [JsonPropertyName("attachment")] public SendAttachmentInput? Attachment { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class SendAttachmentInput
{
    [JsonPropertyName("uploadID")] public string? UploadID { get; set; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("size")] public AttachmentSizeInput? Size { get; set; }
    [JsonPropertyName("duration")] public double? Duration { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class AttachmentSizeInput
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

public class SendMessageOutput
{
    [JsonPropertyName("chatID")] public string? ChatID { get; set; }
    [JsonPropertyName("pendingMessageID")] public string? PendingMessageID { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class EditMessageOutput
{
    [JsonPropertyName("chatID")] public string? ChatID { get; set; }
    [JsonPropertyName("messageID")] public string? MessageID { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class AddReactionOutput
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("chatID")] public string? ChatID { get; set; }
    [JsonPropertyName("messageID")] public string? MessageID { get; set; }
    [JsonPropertyName("reactionKey")] public string? ReactionKey { get; set; }
    [JsonPropertyName("transactionID")] public string? TransactionID { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class RemoveReactionOutput
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("chatID")] public string? ChatID { get; set; }
    [JsonPropertyName("messageID")] public string? MessageID { get; set; }
    [JsonPropertyName("reactionKey")] public string? ReactionKey { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class UploadAssetOutput
{
    [JsonPropertyName("uploadID")] public string? UploadID { get; set; }
    [JsonPropertyName("srcURL")] public string? SrcURL { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
    [JsonPropertyName("fileSize")] public long? FileSize { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("duration")] public double? Duration { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class DownloadAssetOutput
{
    [JsonPropertyName("srcURL")] public string? SrcURL { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class FocusAppInput
{
    [JsonPropertyName("chatID")] public string? ChatID { get; set; }
    [JsonPropertyName("messageID")] public string? MessageID { get; set; }
    [JsonPropertyName("draftText")] public string? DraftText { get; set; }
    [JsonPropertyName("draftAttachmentPath")] public string? DraftAttachmentPath { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class FocusOutput
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class UnifiedSearchOutput
{
    [JsonPropertyName("results")] public JsonElement? Results { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class SearchMessagesOutput
{
    [JsonPropertyName("items")] public List<BeeperMessage>? Items { get; set; }
    [JsonPropertyName("chats")] public List<BeeperChat>? Chats { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
    [JsonPropertyName("oldestCursor")] public string? OldestCursor { get; set; }
    [JsonPropertyName("newestCursor")] public string? NewestCursor { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class SetReminderInput
{
    [JsonPropertyName("reminder")] public ReminderData? Reminder { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class ReminderData
{
    [JsonPropertyName("remindAtMs")] public long RemindAtMs { get; set; }
    [JsonPropertyName("dismissOnIncomingMessage")] public bool DismissOnIncomingMessage { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class AccountsResponse
{
    [JsonPropertyName("items")] public List<BeeperAccount>? Items { get; set; }
    [JsonPropertyName("accounts")] public List<BeeperAccount>? Accounts { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class AuthLoginResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class AuthVerifyResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("userID")] public string? UserID { get; set; }
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("homeserverURL")] public string? HomeserverURL { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class AuthStatusResponse
{
    [JsonPropertyName("loggedIn")] public bool LoggedIn { get; set; }
    [JsonPropertyName("userID")] public string? UserID { get; set; }
    [JsonPropertyName("homeserver")] public string? Homeserver { get; set; }
    [JsonPropertyName("roomCount")] public int RoomCount { get; set; }
    [JsonPropertyName("accountCount")] public int AccountCount { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class RecoveryKeyResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("keysImported")] public int KeysImported { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class VerificationStartResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("txnID")] public string? TxnID { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class VerificationStatusResponse
{
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("txnID")] public string? TxnID { get; set; }
    [JsonPropertyName("emojis")] public List<SASEmojiItem>? Emojis { get; set; }
    [JsonPropertyName("decimals")] public List<int>? Decimals { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class SASEmojiItem
{
    [JsonPropertyName("emoji")] public string? Emoji { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class VerificationConfirmResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
