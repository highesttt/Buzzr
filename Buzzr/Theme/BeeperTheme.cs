using System.Collections.Concurrent;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Buzzr.Theme;

public static class T
{
    public static readonly Color Base       = C("#1F2021");
    public static readonly Color Surface    = C("#242424");
    public static readonly Color SurfaceAlt = C("#2B2B2B");
    public static readonly Color BorderClr  = C("#1A1E20");
    public static readonly Color Hover      = C("#323232");
    public static readonly Color Selected   = C("#2D2D2D");
    public static readonly Color Accent      = GetSystemColor(Windows.UI.ViewManagement.UIColorType.Accent);
    public static readonly Color AccentDark  = GetSystemColor(Windows.UI.ViewManagement.UIColorType.AccentDark1);
    public static readonly Color AccentDark2 = GetSystemColor(Windows.UI.ViewManagement.UIColorType.AccentDark2);
    public static readonly Color AccentLight = GetSystemColor(Windows.UI.ViewManagement.UIColorType.AccentLight1);
    public static readonly Color AccentLight2= GetSystemColor(Windows.UI.ViewManagement.UIColorType.AccentLight2);
    public static readonly Color Fg1        = C("#F3F3F3");
    public static readonly Color Fg2        = C("#A1A1A1");
    public static readonly Color Fg3        = C("#636363");
    public static readonly Color Err        = C("#FF4343");
    public static readonly Color Ok         = C("#6CCB5F");
    public static readonly Color AccentSoft = Color.FromArgb(100, Accent.R, Accent.G, Accent.B);
    public static readonly Color SentBg     = Accent;
    public static readonly Color SentFg     = GetContrastForeground(Accent);
    public static readonly Color Black      = C("#000000");
    public static readonly Color White      = C("#FFFFFF");

    private static Color GetSystemColor(Windows.UI.ViewManagement.UIColorType type)
    {
        try
        {
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            var c = uiSettings.GetColorValue(type);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch
        {
            return C("#4CC2FF");
        }
    }

    public static Color DarkenColor(Color c, double factor)
    {
        return Color.FromArgb(c.A,
            (byte)(c.R * factor),
            (byte)(c.G * factor),
            (byte)(c.B * factor));
    }

    private static Color GetContrastForeground(Color bg)
    {
        var luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return luminance > 0.5 ? C("#000000") : C("#FFFFFF");
    }

    static readonly Dictionary<string, Color> NetColors = new() {
        ["imessage"] = C("#34C759"), ["whatsapp"] = C("#25D366"), ["signal"] = C("#3A76F0"),
        ["telegram"] = C("#0088CC"), ["sms"] = C("#FF9500"), ["discord"] = C("#5865F2"),
        ["instagram"] = C("#E4405F"), ["facebook"] = C("#1877F2"), ["messenger"] = C("#1877F2"),
        ["slack"] = C("#4A154B"), ["linkedin"] = C("#0A66C2"), ["twitter"] = C("#1DA1F2"),
        ["googlechat"] = C("#1A73E8"), ["gmessages"] = C("#1A73E8"), ["line"] = C("#06C755"),
    };

    static readonly Dictionary<string, string> NetNames = new() {
        ["imessage"] = "iMessage", ["whatsapp"] = "WhatsApp", ["signal"] = "Signal",
        ["telegram"] = "Telegram", ["sms"] = "SMS", ["discord"] = "Discord",
        ["instagram"] = "Instagram", ["facebook"] = "Messenger", ["messenger"] = "Messenger",
        ["slack"] = "Slack", ["linkedin"] = "LinkedIn", ["twitter"] = "X",
        ["googlechat"] = "Google", ["gmessages"] = "Google", ["hungryserv"] = "Beeper",
        ["line"] = "Line",
    };

    static readonly Dictionary<string, string> NetLabels = new() {
        ["whatsapp"] = "W", ["telegram"] = "T", ["signal"] = "S",
        ["discord"] = "D", ["instagram"] = "IG", ["facebook"] = "M",
        ["messenger"] = "M", ["imessage"] = "iM", ["sms"] = "SMS",
        ["gmessages"] = "G", ["slack"] = "#", ["twitter"] = "X",
        ["linkedin"] = "in", ["googlechat"] = "G", ["line"] = "L",
        ["hungryserv"] = "B",
    };

    static readonly Dictionary<string, string> IconFileAliases = new() {
        ["hungryserv"] = "beeper",
        ["facebook"] = "messenger",
    };

    public static string ResolveNetwork(string accountId, string? network = null)
    {
        var raw = !string.IsNullOrEmpty(network) ? network.ToLowerInvariant() : accountId.ToLowerInvariant();
            if (raw.StartsWith("sh-"))
        {
            var afterSh = raw[3..];
            var dash = afterSh.IndexOf('-');
            return dash > 0 ? afterSh[..dash] : afterSh;
        }
        if (!string.IsNullOrEmpty(network))
        {
            if (NetColors.ContainsKey(raw)) return raw;
            foreach (var key in NetColors.Keys)
                if (raw.Contains(key)) return key;
            if (raw.Contains("beeper") || raw.Contains("hungryserv")) return "hungryserv";
        }
        var id = accountId.ToLowerInvariant();
        foreach (var key in NetColors.Keys)
            if (id.Contains(key)) return key;
        if (id.Contains("hungryserv")) return "hungryserv";
        return id.Split('_').FirstOrDefault() ?? "";
    }

    public static Color NetColor(string accountId, string? network = null)
    {
        var net = ResolveNetwork(accountId, network);
        if (NetColors.TryGetValue(net, out var color)) return color;
        foreach (var kv in NetColors)
            if (net.Contains(kv.Key)) return kv.Value;
        return Fg2;
    }

    public static string NetName(string accountId, string? network = null)
    {
        var net = ResolveNetwork(accountId, network);
        if (NetNames.TryGetValue(net, out var name)) return name;
        foreach (var kv in NetNames)
            if (net.Contains(kv.Key)) return kv.Value;
        if (!string.IsNullOrEmpty(net))
            return char.ToUpper(net[0]) + net[1..];
        return accountId.Split('_').FirstOrDefault() ?? "Chat";
    }

    static readonly ConcurrentDictionary<string, BitmapImage> _iconCache = new();

    public static Border NetIcon(string accountId, double size = 32, string? network = null)
    {
        var net = ResolveNetwork(accountId, network);
        var bgColor = NetColor(accountId, network);
        var darkBgs = new HashSet<string> { "slack", "discord" };
        var fgColor = darkBgs.Contains(net) ? White : Black;

        var container = new Grid();

        var label = NetLabels.TryGetValue(net, out var lbl)
            ? lbl
            : (string.IsNullOrEmpty(net) ? "?" : net[0].ToString().ToUpper());
        var fontSize = label.Length >= 3 ? size * 0.3
                     : label.Length == 2 ? size * 0.38
                     : size * 0.45;
        container.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = B(fgColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            FontFamily = new FontFamily("Segoe UI"),
        });

        var fileName = IconFileAliases.TryGetValue(net, out var alias) ? alias : net;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NetworkIcons", $"{fileName}.png");
        if (File.Exists(iconPath))
        {
            var bmp = _iconCache.GetOrAdd(net, _ =>
            {
                var b = new BitmapImage { DecodePixelWidth = 64, DecodePixelHeight = 64 };
                try { b.UriSource = new Uri(iconPath); }
                catch { }
                return b;
            });
            var alreadyLoaded = bmp.PixelWidth > 0;
            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.UniformToFill,
                Opacity = alreadyLoaded ? 1 : 0,
            };
            img.ImageOpened += (s, _) =>
            {
                var image = (Image)s;
                image.Opacity = 1;
                if (image.Parent is Grid g && g.Children[0] is TextBlock tb)
                    tb.Visibility = Visibility.Collapsed;
            };
            img.ImageFailed += (s, _) => ((Image)s).Visibility = Visibility.Collapsed;
            container.Children.Add(img);
            if (alreadyLoaded && container.Children[0] is TextBlock fallback)
                fallback.Visibility = Visibility.Collapsed;
        }

        var border = new Border
        {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = B(bgColor),
            Child = container,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.Loaded += (s, _) =>
        {
            var b = (Border)s;
            b.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, size, size),
            };
        };
        return border;
    }

    public static SolidColorBrush B(Color c) => new(c);
    public static SolidColorBrush B(string hex) => new(C(hex));
    public static SolidColorBrush Transparent => new(Colors.Transparent);

    public static TextBlock Lbl(string text, double size = 14, Color? color = null,
        bool bold = false, HorizontalAlignment align = HorizontalAlignment.Left,
        Thickness? margin = null, int maxLines = 0)
    {
        var tb = new TextBlock {
            Text = text, FontSize = size, Foreground = B(color ?? Fg1),
            FontFamily = new FontFamily("Segoe UI"),
            HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center
        };
        if (bold) tb.FontWeight = FontWeights.SemiBold;
        if (margin.HasValue) tb.Margin = margin.Value;
        if (maxLines > 0) { tb.MaxLines = maxLines; tb.TextTrimming = TextTrimming.CharacterEllipsis; }
        return tb;
    }

    public static Border Avatar(string name, double size = 40, Color? bg = null)
    {
        var letter = string.IsNullOrEmpty(name) ? "?" : name[0].ToString().ToUpper();
        return new Border {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = B(bg ?? SurfaceAlt),
            Child = Lbl(letter, size * 0.4, Fg2, true, HorizontalAlignment.Center)
        };
    }

    public static Border Badge(int count)
    {
        return new Border {
            Background = B(Accent), CornerRadius = new CornerRadius(9),
            MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0),
            Child = Lbl(count > 99 ? "99+" : count.ToString(), 10, Black, true, HorizontalAlignment.Center),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static Border NetDot(string accountId, double size = 10, string? network = null)
    {
        return new Border {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = B(NetColor(accountId, network))
        };
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        return new Border {
            Background = B(SurfaceAlt), BorderBrush = B(BorderClr),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = padding ?? new Thickness(24, 20, 24, 20), Child = child
        };
    }

    public static Button AccentBtn(string text)
    {
        return new Button {
            Content = Lbl(text, 14, Black, true, HorizontalAlignment.Center),
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 36,
            Background = B(Accent), Foreground = B(Black),
            CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 4, 0, 0)
        };
    }

    public static Border Divider() => new() {
        Height = 1, Background = B(BorderClr), Margin = new Thickness(0, 4, 0, 4)
    };

    public static string RelativeTime(string? isoTimestamp)
    {
        if (string.IsNullOrEmpty(isoTimestamp) || !DateTimeOffset.TryParse(isoTimestamp, out var dto))
            return "";
        var diff = DateTimeOffset.Now - dto;
        if (diff.TotalMinutes < 1) return "Now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalDays < 1) return dto.ToLocalTime().ToString("h:mm tt");
        if (diff.TotalDays < 2) return "Yesterday";
        if (diff.TotalDays < 7) return dto.ToLocalTime().ToString("ddd");
        if (dto.Year == DateTimeOffset.Now.Year) return dto.ToLocalTime().ToString("MMM d");
        return dto.ToLocalTime().ToString("MMM d, yyyy");
    }

    public static string MessageTime(string? isoTimestamp)
    {
        if (string.IsNullOrEmpty(isoTimestamp) || !DateTimeOffset.TryParse(isoTimestamp, out var dto))
            return "";
        return dto.ToLocalTime().ToString("h:mm tt");
    }

    public static string DateHeader(string? isoTimestamp)
    {
        if (string.IsNullOrEmpty(isoTimestamp) || !DateTimeOffset.TryParse(isoTimestamp, out var dto))
            return "";
        var local = dto.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return "Today";
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("dddd");
        return local.ToString("MMMM d, yyyy");
    }

    static Color C(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(255,
            byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
    }
}
