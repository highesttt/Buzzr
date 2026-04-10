using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeperWinUI.Services;

public class GifService
{
    private static readonly string[] BaseUrls = [
        "https://api.klipy.com/api/v1",
        "https://api.klipy.co/api/v1"
    ];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string? LastError { get; private set; }
    public static bool HasApiKey => !string.IsNullOrEmpty(App.Settings.GifApiKey);

    public static async Task<List<GifResult>> SearchAsync(string query, int limit = 24)
    {
        var key = App.Settings.GifApiKey;
        if (string.IsNullOrEmpty(key)) return [];
        foreach (var baseUrl in BaseUrls)
        {
            var url = $"{baseUrl}/{key}/gifs/search?q={Uri.EscapeDataString(query)}&per_page={limit}&customer_id=beeper&content_filter=medium";
            var results = await FetchAsync(url);
            if (results.Count > 0) return results;
            if (LastError == null) return [];
        }
        return [];
    }

    public static async Task<List<GifResult>> GetTrendingAsync(int limit = 24)
    {
        var key = App.Settings.GifApiKey;
        if (string.IsNullOrEmpty(key)) return [];
        foreach (var baseUrl in BaseUrls)
        {
            var url = $"{baseUrl}/{key}/gifs/trending?per_page={limit}&customer_id=beeper";
            var results = await FetchAsync(url);
            if (results.Count > 0) return results;
            if (LastError == null) return [];
        }
        return [];
    }

    private static async Task<List<GifResult>> FetchAsync(string url)
    {
        LastError = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = await http.GetStringAsync(url);
            var envelope = JsonSerializer.Deserialize<KlipyEnvelope>(body, Json);
            if (envelope == null)
            {
                LastError = "Empty response";
                return [];
            }
            if (!envelope.Result)
            {
                LastError = envelope.Errors?.Message?.FirstOrDefault() ?? "API returned an error";
                return [];
            }
            return ParseResults(envelope);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return [];
        }
    }

    private static List<GifResult> ParseResults(KlipyEnvelope envelope)
    {
        if (envelope.Data?.Items == null) return [];
        return envelope.Data.Items.Select(r =>
        {
            var preview = r.File?.Sm?.Gif ?? r.File?.Xs?.Gif ?? r.File?.Sm?.Webp ?? r.File?.Xs?.Webp;
            var full = r.File?.Hd?.Gif ?? r.File?.Md?.Gif ?? r.File?.Hd?.Webp ?? r.File?.Md?.Webp;
            return new GifResult
            {
                Id = r.Slug ?? r.Id.GetRawText(),
                Title = r.Title ?? "",
                PreviewUrl = preview?.Url ?? "",
                FullUrl = full?.Url ?? "",
                Width = full?.Width ?? 200,
                Height = full?.Height ?? 150
            };
        }).Where(g => !string.IsNullOrEmpty(g.PreviewUrl)).ToList();
    }
}

public class KlipyEnvelope
{
    [JsonPropertyName("result")] public bool Result { get; set; }
    [JsonPropertyName("data")] public KlipyPage? Data { get; set; }
    [JsonPropertyName("errors")] public KlipyErrors? Errors { get; set; }
}

public class KlipyErrors
{
    [JsonPropertyName("message")] public List<string>? Message { get; set; }
}

public class KlipyPage
{
    [JsonPropertyName("data")] public List<KlipyItem>? Items { get; set; }
    [JsonPropertyName("has_next")] public bool HasNext { get; set; }
}

public class KlipyItem
{
    [JsonPropertyName("id")] public JsonElement Id { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("file")] public KlipySizes? File { get; set; }
}

public class KlipySizes
{
    [JsonPropertyName("hd")] public KlipyFormats? Hd { get; set; }
    [JsonPropertyName("md")] public KlipyFormats? Md { get; set; }
    [JsonPropertyName("sm")] public KlipyFormats? Sm { get; set; }
    [JsonPropertyName("xs")] public KlipyFormats? Xs { get; set; }
}

public class KlipyFormats
{
    [JsonPropertyName("gif")] public KlipyMedia? Gif { get; set; }
    [JsonPropertyName("webp")] public KlipyMedia? Webp { get; set; }
    [JsonPropertyName("mp4")] public KlipyMedia? Mp4 { get; set; }
}

public class KlipyMedia
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class GifResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string PreviewUrl { get; set; } = "";
    public string FullUrl { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}
