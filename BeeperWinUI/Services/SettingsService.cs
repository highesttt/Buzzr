using System.Text.Json;

namespace BeeperWinUI.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private Dictionary<string, string> _settings;

    private const string KeyHomeserver = "homeserver_url";
    private const string KeyAccessToken = "access_token";
    private const string KeyUserId = "user_id";
    private const string KeyDeviceId = "device_id";
    private const string KeySyncToken = "sync_token";
    private const string KeyTheme = "theme";
    private const string KeyNotifications = "notifications_enabled";
    private const string KeyGifApiKey = "gif_api_key";

    public SettingsService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeeperWinUI");
        Directory.CreateDirectory(appDataDir);
        _settingsPath = Path.Combine(appDataDir, "settings.json");
        _settings = Load();
    }

    public bool HasSession =>
        !string.IsNullOrEmpty(GetString(KeyAccessToken)) &&
        !string.IsNullOrEmpty(GetString(KeyUserId));

    public string? HomeserverUrl
    {
        get => GetString(KeyHomeserver);
        set => SetString(KeyHomeserver, value);
    }

    public string? AccessToken
    {
        get => GetString(KeyAccessToken);
        set => SetString(KeyAccessToken, value);
    }

    public string? UserId
    {
        get => GetString(KeyUserId);
        set => SetString(KeyUserId, value);
    }

    public string? DeviceId
    {
        get => GetString(KeyDeviceId);
        set => SetString(KeyDeviceId, value);
    }

    public string? SyncToken
    {
        get => GetString(KeySyncToken);
        set => SetString(KeySyncToken, value);
    }

    public string? GifApiKey
    {
        get => GetString(KeyGifApiKey);
        set => SetString(KeyGifApiKey, value);
    }

    public bool NotificationsEnabled
    {
        get => GetString(KeyNotifications) == "true";
        set => SetString(KeyNotifications, value ? "true" : "false");
    }

    public string Theme
    {
        get => GetString(KeyTheme) ?? "dark";
        set => SetString(KeyTheme, value);
    }

    public void SaveSession(string homeserverUrl, string accessToken, string userId, string? deviceId)
    {
        HomeserverUrl = homeserverUrl;
        AccessToken = accessToken;
        UserId = userId;
        DeviceId = deviceId;
    }

    public void ClearSession()
    {
        HomeserverUrl = null;
        AccessToken = null;
        UserId = null;
        DeviceId = null;
        SyncToken = null;
    }

    private string? GetString(string key) =>
        _settings.TryGetValue(key, out var value) ? value : null;

    private void SetString(string key, string? value)
    {
        if (value == null) _settings.Remove(key);
        else _settings[key] = value;
        Save();
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            }
        }
        catch { }
        return [];
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }
}
