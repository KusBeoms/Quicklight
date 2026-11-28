using System.Text.RegularExpressions;

namespace Quicklight.Core.Ai;

/// <param name="Language">English name of the target language (for the prompt).</param>
/// <param name="Label">Korean name of the target language (for the card).</param>
public sealed record TranslateRequest(string Text, string Language, string Label);

/// <summary>"번역 안녕", "tr hello", "안녕 영어로", "hello 일본어로", "translate hi to japanese".</summary>
public static partial class TranslateParser
{
    static readonly Dictionary<string, (string En, string Ko)> Langs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["한국어"] = ("Korean", "한국어"), ["한글"] = ("Korean", "한국어"), ["korean"] = ("Korean", "한국어"),
        ["영어"] = ("English", "영어"), ["english"] = ("English", "영어"),
        ["일본어"] = ("Japanese", "일본어"), ["japanese"] = ("Japanese", "일본어"),
        ["중국어"] = ("Chinese", "중국어"), ["chinese"] = ("Chinese", "중국어"),
    };

    [GeneratedRegex(@"^(?:번역|tr|translate)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^(.+?)\s+(?:(?:to|into)\s+(?<l>[a-z]+)|(?<l>한국어|한글|영어|일본어|중국어)(?:으?로)(?:\s*번역)?)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Suffix();

    public static TranslateRequest? Parse(string query)
    {
        var q = query.Trim();
        bool keyword = false;
        if (Prefix().Match(q) is { Success: true } p) { q = p.Groups[1].Value.Trim(); keyword = true; }
        (string En, string Ko)? lang = null;
        if (Suffix().Match(q) is { Success: true } s && Langs.TryGetValue(s.Groups["l"].Value, out var l))
        {
            lang = l;
            q = s.Groups[1].Value.Trim();
        }
        else if (!keyword) return null; // no keyword and no target language: an ordinary search
        if (q.Length == 0) return null;
        lang ??= q.Any(c => c is >= '가' and <= '힣') ? Langs["영어"] : Langs["한국어"];
        return new TranslateRequest(q, lang.Value.En, lang.Value.Ko);
    }
}
