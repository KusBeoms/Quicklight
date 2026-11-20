using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Quicklight.Siri;

/// <summary>
/// Answer text as the panel shows it: paragraphs, headings, bullet and numbered lists, **bold**, `code`, links, code
/// blocks and tables (cells 8, columns at least 96 wide, scrolling sideways when narrow). Only what a short answer needs.
/// </summary>
public static partial class MarkdownView
{
    public const double BodySize = 14, BodyLine = 21, TitleSize = 17, TitleLine = 23;

    public static UIElement Build(string text, FrameworkElement scope)
    {
        var stack = new StackPanel();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }

            if (line.TrimStart().StartsWith("```"))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i]);
                i++;
                Add(stack, CodeBlock(string.Join("\n", code), scope));
                continue;
            }
            if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1]))
            {
                var rows = new List<string[]> { Cells(line) };
                for (i += 2; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++) rows.Add(Cells(lines[i]));
                Add(stack, Table(rows, scope));
                continue;
            }
            var heading = Heading().Match(line);
            if (heading.Success)
            {
                Add(stack, Paragraph(heading.Groups[1].Value, scope, TitleSize, TitleLine, FontWeights.SemiBold, bottom: 6));
                i++;
                continue;
            }
            var item = ListItem().Match(line);
            if (item.Success)
            {
                Add(stack, Bullet(item.Groups[2].Value.Length > 0 ? item.Groups[2].Value : "•", item.Groups[3].Value, scope));
                i++;
                continue;
            }
            // A paragraph runs to the next blank line or block.
            var para = new List<string> { line.Trim() };
            for (i++; i < lines.Length && lines[i].Trim().Length > 0 && !IsBlockStart(lines, i); i++) para.Add(lines[i].Trim());
            Add(stack, Paragraph(string.Join(" ", para), scope, BodySize, BodyLine, FontWeights.Normal));
        }
        return stack;
    }

    static bool IsBlockStart(string[] lines, int i) =>
        lines[i].TrimStart().StartsWith("```") || Heading().IsMatch(lines[i]) || ListItem().IsMatch(lines[i]) ||
        (lines[i].TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1]));

    static void Add(StackPanel stack, UIElement e) => stack.Children.Add(e);

    static TextBlock Paragraph(string text, FrameworkElement scope, double size, double line, FontWeight weight, double bottom = 10)
    {
        var tb = new TextBlock
        {
            FontSize = size, LineHeight = line, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AddInlines(tb.Inlines, text, scope);
        return tb;
    }

    static UIElement Bullet(string marker, string text, FrameworkElement scope)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new TextBlock { Text = marker, FontSize = BodySize, LineHeight = BodyLine, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        grid.Children.Add(dot);
        var body = Paragraph(text, scope, BodySize, BodyLine, FontWeights.Normal, bottom: 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    static UIElement CodeBlock(string code, FrameworkElement scope)
    {
        var tb = new TextBlock { Text = code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 10), Child = tb };
        border.SetResourceReference(Border.BackgroundProperty, "GlyphBackBrush");
        return border;
    }

    static UIElement Table(List<string[]> rows, FrameworkElement scope)
    {
        int cols = rows.Max(r => r.Length);
        var grid = new Grid();
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 96, Width = GridLength.Auto });
        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            for (int c = 0; c < cols; c++)
            {
                var cell = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(8) };
                cell.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
                var tb = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal };
                tb.SetResourceReference(TextBlock.ForegroundProperty, r == 0 ? "SubtleTextBrush" : "TextBrush");
                AddInlines(tb.Inlines, c < rows[r].Length ? rows[r][c] : "", scope);
                cell.Child = tb;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        // A narrow panel scrolls the table sideways instead of squeezing the columns.
        return new ScrollViewer
        {
            Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 10), Focusable = false,
        };
    }

    static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    static void AddInlines(InlineCollection inlines, string text, FrameworkElement scope)
    {
        int pos = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            if (m.Groups[1].Success) inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.SemiBold });
            else if (m.Groups[2].Success)
            {
                var code = new Run(m.Groups[2].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
                code.SetResourceReference(TextElement.BackgroundProperty, "GlyphBackBrush");
                inlines.Add(code);
            }
            else
            {
                var url = m.Groups[4].Value;
                var link = new Hyperlink(new Run(m.Groups[3].Value)) { ToolTip = url };
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                // Only web links open: a model's text must not start arbitrary programs.
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); } catch { } };
                inlines.Add(link);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex Inline();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.*)$")]
    private static partial Regex Heading();

    // Group 2 is the marker for numbered items ("1."), empty for bullets, so the caller can draw "•" itself.
    static readonly Regex ListItemRegex = new(@"^\s*(?:([-*•])|(\d+[.)]))\s+(.*)$", RegexOptions.Compiled);
    static Regex ListItem() => ListItemRegex;

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();
}
