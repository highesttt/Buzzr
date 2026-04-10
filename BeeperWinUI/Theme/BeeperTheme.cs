using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace BeeperWinUI.Theme;

public static class T
{
    public static readonly Color Base       = C("#1F2021");
    public static readonly Color Surface    = C("#242424");
    public static readonly Color SurfaceAlt = C("#2B2B2B");
    public static readonly Color BorderClr  = C("#1A1E20");
    public static readonly Color Hover      = C("#323232");
    public static readonly Color Selected   = C("#2D2D2D");
    public static readonly Color Accent     = C("#4CC2FF");
    public static readonly Color AccentDark = C("#3BA3D9");
    public static readonly Color Fg1        = C("#F3F3F3");
    public static readonly Color Fg2        = C("#A1A1A1");
    public static readonly Color Fg3        = C("#636363");
    public static readonly Color Err        = C("#FF4343");
    public static readonly Color Ok         = C("#6CCB5F");
    public static readonly Color SentBg     = C("#4CC2FF");
    public static readonly Color SentFg     = C("#000000");
    public static readonly Color Black      = C("#000000");

    static readonly Dictionary<string, Color> NetColors = new() {
        ["imessage"] = C("#34C759"), ["whatsapp"] = C("#25D366"), ["signal"] = C("#3A76F0"),
        ["telegram"] = C("#0088CC"), ["sms"] = C("#FF9500"), ["discord"] = C("#5865F2"),
        ["instagram"] = C("#E4405F"), ["facebook"] = C("#1877F2"), ["messenger"] = C("#1877F2"),
        ["slack"] = C("#4A154B"), ["linkedin"] = C("#0A66C2"), ["twitter"] = C("#1DA1F2"),
        ["googlechat"] = C("#1A73E8"), ["gmessages"] = C("#1A73E8"),
    };

    public static Color NetColor(string accountId)
    {
        var id = accountId.ToLowerInvariant();
        foreach (var kv in NetColors)
            if (id.Contains(kv.Key)) return kv.Value;
        return Fg2;
    }

    public static string NetName(string accountId)
    {
        var id = accountId.ToLowerInvariant();
        if (id.Contains("imessage")) return "iMessage";
        if (id.Contains("whatsapp")) return "WhatsApp";
        if (id.Contains("signal")) return "Signal";
        if (id.Contains("telegram")) return "Telegram";
        if (id.Contains("sms")) return "SMS";
        if (id.Contains("discord")) return "Discord";
        if (id.Contains("instagram")) return "Instagram";
        if (id.Contains("facebook") || id.Contains("messenger")) return "Messenger";
        if (id.Contains("slack")) return "Slack";
        if (id.Contains("linkedin")) return "LinkedIn";
        if (id.Contains("twitter")) return "X";
        if (id.Contains("googlechat") || id.Contains("gmessages")) return "Google";
        if (id.Contains("hungryserv")) return "Beeper";
        return accountId.Split('_').FirstOrDefault() ?? "Chat";
    }

    public static string NetGlyph(string accountId)
    {
        var id = accountId.ToLowerInvariant();
        if (id.Contains("sms") || id.Contains("gmessages")) return "\uE715";
        if (id.Contains("imessage")) return "\uE715";
        if (id.Contains("discord") || id.Contains("slack") || id.Contains("telegram")
            || id.Contains("whatsapp") || id.Contains("signal")) return "\uE717";
        if (id.Contains("instagram") || id.Contains("facebook") || id.Contains("messenger")
            || id.Contains("twitter") || id.Contains("linkedin")) return "\uE77B";
        return "\uE774";
    }

    public static string? NetPathData(string accountId)
    {
        var id = accountId.ToLowerInvariant();
        if (id.Contains("whatsapp")) return "M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347m-5.421 7.403h-.004a9.87 9.87 0 01-5.031-1.378l-.361-.214-3.741.982.998-3.648-.235-.374a9.86 9.86 0 01-1.51-5.26c.001-5.45 4.436-9.884 9.888-9.884 2.64 0 5.122 1.03 6.988 2.898a9.825 9.825 0 012.893 6.994c-.003 5.45-4.437 9.884-9.885 9.884m8.413-18.297A11.815 11.815 0 0012.05 0C5.495 0 .16 5.335.157 11.892c0 2.096.547 4.142 1.588 5.945L.057 24l6.305-1.654a11.882 11.882 0 005.683 1.448h.005c6.554 0 11.89-5.335 11.893-11.893a11.821 11.821 0 00-3.48-8.413z";
        if (id.Contains("telegram")) return "M11.944 0A12 12 0 000 12a12 12 0 0012 12 12 12 0 0012-12A12 12 0 0012 0a12 12 0 00-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 01.171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.479.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z";
        if (id.Contains("signal")) return "M12 0C5.373 0 0 5.373 0 12s5.373 12 12 12 12-5.373 12-12S18.627 0 12 0zm5.894 16.834a.75.75 0 01-1.06.026l-2.894-2.726V17.5a.75.75 0 01-1.5 0v-5a.752.752 0 01.218-.53l3.5-3.5a.75.75 0 111.06 1.06L14.44 12.31l2.894 2.726-.026 1.06.586.738zm-4.394-4.334a.75.75 0 01-1.5 0V7.25a.75.75 0 011.5 0v5.25zM9.75 17.5a.75.75 0 01-1.5 0v-4.134l-2.894 2.726a.75.75 0 11-1.028-1.092l3.5-3.5a.75.75 0 01.53-.218H9v-.032a.75.75 0 01.75.75v5.5z";
        if (id.Contains("discord")) return "M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189z";
        if (id.Contains("instagram")) return "M12 0C8.74 0 8.333.015 7.053.072 5.775.132 4.905.333 4.14.63c-.789.306-1.459.717-2.126 1.384S.935 3.35.63 4.14C.333 4.905.131 5.775.072 7.053.012 8.333 0 8.74 0 12s.015 3.667.072 4.947c.06 1.277.261 2.148.558 2.913.306.788.717 1.459 1.384 2.126.667.666 1.336 1.079 2.126 1.384.766.296 1.636.499 2.913.558C8.333 23.988 8.74 24 12 24s3.667-.015 4.947-.072c1.277-.06 2.148-.262 2.913-.558.788-.306 1.459-.718 2.126-1.384.666-.667 1.079-1.335 1.384-2.126.296-.765.499-1.636.558-2.913.06-1.28.072-1.687.072-4.947s-.015-3.667-.072-4.947c-.06-1.277-.262-2.149-.558-2.913-.306-.789-.718-1.459-1.384-2.126C21.319 1.347 20.651.935 19.86.63c-.765-.297-1.636-.499-2.913-.558C15.667.012 15.26 0 12 0zm0 2.16c3.203 0 3.585.016 4.85.071 1.17.055 1.805.249 2.227.415.562.217.96.477 1.382.896.419.42.679.819.896 1.381.164.422.36 1.057.413 2.227.057 1.266.07 1.646.07 4.85s-.015 3.585-.074 4.85c-.061 1.17-.256 1.805-.421 2.227-.224.562-.479.96-.899 1.382-.419.419-.824.679-1.38.896-.42.164-1.065.36-2.235.413-1.274.057-1.649.07-4.859.07-3.211 0-3.586-.015-4.859-.074-1.171-.061-1.816-.256-2.236-.421-.569-.224-.96-.479-1.379-.899-.421-.419-.69-.824-.9-1.38-.165-.42-.359-1.065-.42-2.235-.045-1.26-.061-1.649-.061-4.844 0-3.196.016-3.586.061-4.861.061-1.17.255-1.814.42-2.234.21-.57.479-.96.9-1.381.419-.419.81-.689 1.379-.898.42-.166 1.051-.361 2.221-.421 1.275-.045 1.65-.06 4.859-.06l.045.03zm0 3.678a6.162 6.162 0 100 12.324 6.162 6.162 0 100-12.324zM12 16c-2.21 0-4-1.79-4-4s1.79-4 4-4 4 1.79 4 4-1.79 4-4 4zm7.846-10.405a1.441 1.441 0 11-2.88 0 1.441 1.441 0 012.88 0z";
        if (id.Contains("facebook") || id.Contains("messenger")) return "M.001 11.639C.001 4.949 5.241 0 12.001 0S24 4.95 24 11.639c0 6.689-5.24 11.638-12 11.638-1.21 0-2.38-.16-3.47-.46a.96.96 0 00-.64.05l-2.39 1.05a.96.96 0 01-1.35-.85l-.07-2.14a.97.97 0 00-.32-.68A11.39 11.389 0 01.002 11.64zm8.32-2.19l-3.52 5.6c-.35.53.32 1.139.82.75l3.79-2.87c.26-.2.6-.2.87 0l2.8 2.1c.84.63 2.04.4 2.6-.48l3.52-5.6c.35-.53-.32-1.13-.82-.75l-3.79 2.87c-.25.2-.6.2-.86 0l-2.8-2.1a1.8 1.8 0 00-2.6.48z";
        if (id.Contains("slack")) return "M5.042 15.165a2.528 2.528 0 01-2.52 2.523A2.528 2.528 0 010 15.165a2.527 2.527 0 012.522-2.52h2.52v2.52zM6.313 15.165a2.527 2.527 0 012.521-2.52 2.527 2.527 0 012.521 2.52v6.313A2.528 2.528 0 018.834 24a2.528 2.528 0 01-2.521-2.522v-6.313zM8.834 5.042a2.528 2.528 0 01-2.521-2.52A2.528 2.528 0 018.834 0a2.528 2.528 0 012.521 2.522v2.52H8.834zM8.834 6.313a2.528 2.528 0 012.521 2.521 2.528 2.528 0 01-2.521 2.521H2.522A2.528 2.528 0 010 8.834a2.528 2.528 0 012.522-2.521h6.312zM18.956 8.834a2.528 2.528 0 012.522-2.521A2.528 2.528 0 0124 8.834a2.528 2.528 0 01-2.522 2.521h-2.522V8.834zM17.688 8.834a2.528 2.528 0 01-2.523 2.521 2.527 2.527 0 01-2.52-2.521V2.522A2.527 2.527 0 0115.165 0a2.528 2.528 0 012.523 2.522v6.312zM15.165 18.956a2.528 2.528 0 012.523 2.522A2.528 2.528 0 0115.165 24a2.527 2.527 0 01-2.52-2.522v-2.522h2.52zM15.165 17.688a2.527 2.527 0 01-2.52-2.523 2.526 2.526 0 012.52-2.52h6.313A2.527 2.527 0 0124 15.165a2.528 2.528 0 01-2.522 2.523h-6.313z";
        if (id.Contains("imessage") || id.Contains("sms") || id.Contains("gmessages")) return "M12 2C6.477 2 2 6.477 2 12c0 1.89.525 3.66 1.438 5.168L2.546 20.2A1.5 1.5 0 003.8 21.454l3.032-.892A9.96 9.96 0 0012 22c5.523 0 10-4.477 10-10S17.523 2 12 2z";
        if (id.Contains("twitter")) return "M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-5.214-6.817L4.99 21.75H1.68l7.73-8.835L1.254 2.25H8.08l4.713 6.231zm-1.161 17.52h1.833L7.084 4.126H5.117z";
        if (id.Contains("linkedin")) return "M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85 3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.062 2.062 0 01-2.063-2.065 2.064 2.064 0 112.063 2.065zm1.782 13.019H3.555V9h3.564v11.452zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0h.003z";
        if (id.Contains("googlechat")) return "M12 0C5.373 0 0 5.373 0 12s5.373 12 12 12 12-5.373 12-12S18.627 0 12 0zm5.562 8.248l-3.559 3.394h-.006v4.983c0 .327-.27.592-.602.592H8.603a.599.599 0 01-.603-.592V11.64l3.559-3.394h.007V3.265c0-.327.27-.593.601-.593h4.793c.332 0 .602.266.602.593v4.983z";
        return null;
    }

    public static FrameworkElement NetLogoElement(string accountId, double size = 14, Color? foreground = null)
    {
        var fg = foreground ?? Black;
        var pathData = NetPathData(accountId);
        if (pathData != null)
        {
            try
            {
                var shape = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
                        typeof(Geometry), pathData),
                    Fill = B(fg),
                    Stretch = Stretch.Uniform,
                    Width = size, Height = size,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return shape;
            }
            catch { }
        }
        return new FontIcon
        {
            Glyph = NetGlyph(accountId),
            FontSize = size,
            Foreground = B(fg),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
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

    public static Border NetDot(string accountId, double size = 10)
    {
        return new Border {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = B(NetColor(accountId))
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
        return dto.ToLocalTime().ToString("MMM d");
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
