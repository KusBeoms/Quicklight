using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Quicklight;

/// <summary>Light/dark brushes that follow Windows (or the settings override) plus the system accent color.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Apply(ResourceDictionary res, string mode)
    {
        IsDark = mode switch
        {
            "dark" => true,
            "light" => false,
            _ => ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") == 0,
        };
        var accent = AccentColor();

        if (IsDark)
        {
            Set(res, "PanelBrush", Color.FromArgb(0x80, 0x1E, 0x1E, 0x22)); // over the blurred screen: it shows through
            Set(res, "MenuBrush", Color.FromArgb(0xFA, 0x2A, 0x2A, 0x30));
            Set(res, "SolidPanelBrush", Color.FromRgb(0x26, 0x26, 0x2B));
            Set(res, "TextBrush", Color.FromRgb(0xF2, 0xF2, 0xF4));
            Set(res, "SubtleTextBrush", Color.FromArgb(0xA8, 0xF2, 0xF2, 0xF4));
            Set(res, "PlaceholderBrush", Color.FromArgb(0x70, 0xF2, 0xF2, 0xF4));
            Set(res, "HeaderBrush", Color.FromArgb(0x90, 0xF2, 0xF2, 0xF4));
            Set(res, "SeparatorBrush", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            Set(res, "GlyphBackBrush", Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            Set(res, "HoverBrush", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Set(res, "BorderBrush", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            Set(res, "PanelBrush", Color.FromArgb(0x90, 0xF7, 0xF7, 0xF9));
            Set(res, "MenuBrush", Color.FromArgb(0xFC, 0xFA, 0xFA, 0xFC));
            Set(res, "SolidPanelBrush", Color.FromRgb(0xF7, 0xF7, 0xF9));
            Set(res, "TextBrush", Color.FromRgb(0x1A, 0x1A, 0x1E));
            Set(res, "SubtleTextBrush", Color.FromArgb(0xA0, 0x1A, 0x1A, 0x1E));
            Set(res, "PlaceholderBrush", Color.FromArgb(0x70, 0x1A, 0x1A, 0x1E));
            Set(res, "HeaderBrush", Color.FromArgb(0x88, 0x1A, 0x1A, 0x1E));
            Set(res, "SeparatorBrush", Color.FromArgb(0x1E, 0x00, 0x00, 0x00));
            Set(res, "GlyphBackBrush", Color.FromArgb(0x16, 0x00, 0x00, 0x00));
            Set(res, "HoverBrush", Color.FromArgb(0x0E, 0x00, 0x00, 0x00));
            Set(res, "BorderBrush", Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        }
        Set(res, "AccentBrush", accent);
        Set(res, "SelectedTextBrush", Colors.White);

        // The selection's tint over the magnified backdrop: lighter and clearer than the panel's own tint.
        Set(res, "GlassFillBrush", IsDark ? Color.FromArgb(0x70, 0x3A, 0x3A, 0x42) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    }

    static void Set(ResourceDictionary res, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        res[key] = b;
    }

    /// <summary>The Windows accent color, darkened a little if it is too light for white text.</summary>
    static Color AccentColor()
    {
        var abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor");
        var c = abgr is { } v
            ? Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF))
            : Color.FromRgb(0x2F, 0x6F, 0xEB);
        // A grey accent (a common Windows choice) makes a dull selection; use the Windows default blue instead.
        byte max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max == 0 || (max - min) / (double)max < 0.2) c = Color.FromRgb(0x00, 0x67, 0xC0);
        // Keep white text readable on the selection.
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        if (luminance > 0.45)
        {
            double k = 0.45 / luminance;
            c = Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }
        return c;
    }

    static int? ReadDword(string key, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) is int i ? i : null;
        }
        catch { return null; }
    }
}
