using System.Text.RegularExpressions;

namespace Quicklight.Core.Ai;

/// <summary>Tells a natural-language file description ("지난주에 받은 견적서 pdf") from an ordinary search.</summary>
public static partial class FileQueryDetector
{
    static readonly string[] TimeWords = ["어제", "그제", "오늘", "지난주", "저번주", "이번주", "지난달", "저번달", "이번달", "작년", "올해", "최근", "얼마 전", "yesterday", "last week", "last month", "last year"];
    static readonly string[] TypeWords = ["pdf", "엑셀", "excel", "xlsx", "사진", "이미지", "그림", "동영상", "영상", "발표", "ppt", "문서", "한글파일", "워드", "word", "docx", "hwp", "파일"];

    [GeneratedRegex(@"(^|\s)\d{1,2}월")] private static partial Regex Month();

    public static bool IsFileQuery(string text)
    {
        var t = text.Trim();
        if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return false;
        var lower = t.ToLowerInvariant();
        return Month().IsMatch(lower) || TimeWords.Any(lower.Contains) && TypeWords.Any(lower.Contains);
    }

    /// <summary>Everything query from the model's answer: code fences and quotes off, one line under 300 chars; null if unusable.</summary>
    public static string? CleanQuery(string? answer)
    {
        var s = (answer ?? "").Trim();
        if (s.StartsWith("```")) s = string.Join('\n', s.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        s = s.Trim().Trim('`', '"', '\'').Trim();
        return s.Length is > 0 and < 300 && !s.Contains('\n') ? s : null;
    }
}
