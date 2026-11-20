using System.Windows.Automation;

namespace Quicklight;

/// <summary>The text selected in whatever has keyboard focus, read through UI Automation (the clipboard is left alone).</summary>
internal static class FocusedSelection
{
    const int MaxChars = 20_000;

    /// <summary>The selected text of the focused control, or null (nothing selected, a password box, a control without text selection, any failure).</summary>
    public static string? GetSelectedText()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.IsPassword || element.Current.ProcessId == Environment.ProcessId) return null; // never our own search box
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            var text = string.Concat(((TextPattern)pattern).GetSelection().Select(r => r.GetText(MaxChars)));
            if (text.Length > MaxChars) text = text[..MaxChars];
            return text.Length == 0 ? null : text;
        }
        catch (Exception) { return null; } // UIA fails in many ways (the app closed, no provider, access denied)
    }
}
