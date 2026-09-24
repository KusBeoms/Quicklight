using System.Text;
using Quicklight.Core.Everything;
using Quicklight.Core.Models;
using Quicklight.Core.Translation;

namespace Quicklight.Mcp;

/// <summary>
/// The launcher's UI as Markdown, for AI clients: no window opens, the answer reads like the Spotlight panel.
/// Direct answers (calculation, conversion, translation) become a big heading; other results are grouped by kind.
/// </summary>
public static class McpMarkdown
{
    static string KindLabel(ResultKind k) => k switch
    {
        ResultKind.Calculator => "계산기",
        ResultKind.Translation => "번역",
        ResultKind.Currency => "환율",
        ResultKind.Url => "웹사이트",
        ResultKind.Path => "경로",
        ResultKind.App => "앱",
        ResultKind.Setting => "설정",
        ResultKind.Command => "명령",
        ResultKind.Folder => "폴더",
        ResultKind.File => "문서",
        ResultKind.WebSearch => "웹 검색",
        _ => k.ToString(),
    };

    static bool IsAnswer(SearchResult r) =>
        r.Kind is ResultKind.Calculator or ResultKind.Currency or ResultKind.Translation && r.Action != ActionType.None;

    public static string Search(string query, IReadOnlyList<SearchResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Quicklight** · `{Code(query)}`").AppendLine();
        if (results.Count == 0) return sb.AppendLine("_결과 없음_").ToString();

        var rest = results;
        if (IsAnswer(results[0]))
        {
            Answer(sb, results[0].Title, results[0].Subtitle);
            rest = results.Skip(1).ToList();
        }
        else
        {
            sb.AppendLine("#### 최상위 결과");
            Row(sb, results[0]);
            sb.AppendLine();
            rest = results.Skip(1).ToList();
        }

        foreach (var group in rest.GroupBy(r => KindLabel(r.Kind)))
        {
            sb.AppendLine($"#### {group.Key}");
            foreach (var r in group) Row(sb, r);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    static void Row(StringBuilder sb, SearchResult r)
    {
        sb.Append("- **").Append(Escape(r.Title)).Append("**");
        var detail = new List<string>();
        if (r.RevealPath is { } p) detail.Add($"`{Code(p)}`");
        else if (!string.IsNullOrWhiteSpace(r.Subtitle)) detail.Add(Escape(r.Subtitle.Replace("   ·   ", " · ")));
        if (r.Modified is { } m) detail.Add(m.ToString("yyyy-MM-dd HH:mm"));
        if (r.Size is { } s) detail.Add(Size(s));
        if (r.RequiresConfirmation) detail.Add("⚠ 실행 전 확인 필요");
        if (detail.Count > 0) sb.Append(" — ").Append(string.Join(" · ", detail));
        sb.AppendLine();
    }

    /// <summary>A direct answer: the value as a heading, its context underneath.</summary>
    public static string Answer(string value, string context)
    {
        var sb = new StringBuilder();
        Answer(sb, value, context);
        return sb.ToString().TrimEnd();
    }

    static void Answer(StringBuilder sb, string value, string context)
    {
        // Short values as a big heading; long text (a translated paragraph) as a quote that keeps its lines.
        if (value.Length <= 60 && !value.Contains('\n')) sb.AppendLine($"# {Escape(value)}");
        else foreach (var line in value.Split('\n')) sb.AppendLine($"> {Escape(line)}");
        if (!string.IsNullOrWhiteSpace(context)) sb.AppendLine().AppendLine(Escape(context.Replace("   ·   ", " · ")));
        sb.AppendLine();
    }

    public static string Files(string query, IReadOnlyList<EverythingItem> items)
    {
        var sb = new StringBuilder().AppendLine($"**Everything** · `{Code(query)}` · {items.Count}개").AppendLine();
        if (items.Count == 0) return sb.AppendLine("_결과 없음_").ToString().TrimEnd();
        sb.AppendLine("| 이름 | 폴더 | 크기 | 수정한 날짜 |").AppendLine("|---|---|---:|---|");
        foreach (var i in items)
            sb.AppendLine($"| {(i.IsFolder ? "📁 " : "")}{Cell(i.Name)} | `{Code(i.Directory)}` | {(i.Size >= 0 ? Size(i.Size) : "")} | {i.Modified:yyyy-MM-dd HH:mm} |");
        return sb.ToString().TrimEnd();
    }

    public static string Translation(TranslationResult r, string original)
    {
        var sb = new StringBuilder();
        Answer(sb, r.Text, $"{Languages.KoreanName(r.Source)} → {Languages.KoreanName(r.Target)} · LibreTranslate (로컬)");
        sb.AppendLine($"원문: {Escape(original)}");
        if (r.Alternatives.Count > 0)
        {
            sb.AppendLine().AppendLine("#### 다른 번역");
            foreach (var a in r.Alternatives) sb.AppendLine($"- {Escape(a)}");
        }
        return sb.ToString().TrimEnd();
    }

    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    /// <summary>Escapes Markdown control characters in plain text.</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (c is '\r' or '\n') { sb.Append(' '); continue; }
            if (IsInvisible(c)) continue;
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|' or '~' or '&') sb.Append('\\');
            sb.Append(c);
        }
        // Text at the start of a line must not turn into a list or a heading underline: "- x", "1. x", "= x".
        var result = sb.ToString();
        int start = result.Length - result.TrimStart(' ', '	').Length; // markers after leading spaces count too
        var rest = result[start..];
        if (rest.Length > 0 && (rest[0] is '-' or '+' or '=' || System.Text.RegularExpressions.Regex.IsMatch(rest, @"^\d+[.)]")))
        {
            int i = start + (rest[0] is '-' or '+' or '=' ? 0 : rest.IndexOfAny(['.', ')']));
            result = result.Insert(i, "\\");
        }
        return result;
    }

    /// <summary>Bidi overrides and zero-width characters can disguise a name ("gpj.exe" shown as "exe.jpg"); they are dropped.</summary>
    static bool IsInvisible(char c) =>
        // Tabs and the zero-width joiners (emoji sequences, Indic and Persian text) are real content; keep them.
        c is not ('	' or '‌' or '‍') &&
        char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control;

    static string Cell(string s) => Escape(s);

    /// <summary>Text inside `code`: backticks cannot be escaped there, so they are replaced.</summary>
    static string Code(string s) =>
        new string(s.Replace('`', '\'').Replace('\n', ' ').Replace('\r', ' ').Where(c => !IsInvisible(c)).ToArray());
}
