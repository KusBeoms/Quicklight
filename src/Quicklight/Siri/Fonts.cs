using System.Windows;
using System.Windows.Media;

namespace Quicklight.Siri;

/// <summary>The bold face of the app's typeface (The Jamsil keeps every weight as a family of its own, so bold is a name, not a FontWeight).</summary>
internal static class Fonts
{
    public static FontFamily Bold => (FontFamily)Application.Current.FindResource("UiFontBold");
}
