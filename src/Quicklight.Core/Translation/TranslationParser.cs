using System.Text.RegularExpressions;

namespace Quicklight.Core.Translation;

/// <param name="Target">Requested language code, or null to let the default rule decide.</param>
public sealed record TranslationRequest(string Text, string? Target);

/// <summary>
/// Recognizes translation requests. A trigger word is required so ordinary searches are never sent to the translator:
/// "hello 번역", "번역 hello", "사과 영어로", "영어로 사과", "good morning in korean", "translate bonjour", "serendipity 뜻".
/// </summary>
public static class TranslationParser
{
    public const int MaxLength = 500;

    static readonly string Lang = string.Join("|", Languages.AllNames.Select(Regex.Escape));
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    static readonly Regex[] Patterns =
    [
        // "사과 영어로", "사과 영어로 번역해줘", "사과를 영어로"
        new($@"^(?<text>.+?)(?:을|를)?\s*(?<lang>{Lang})\s*(?:으로|로)(?:\s*(?:번역|바꿔)(?:해\s*줘|해|하기)?)?\s*$", Opts, Timeout),
        // "영어로 사과", "영어로 번역 사과"
        new($@"^(?<lang>{Lang})\s*(?:으로|로)\s+(?:번역\s+)?(?<text>.+)$", Opts, Timeout),
        // "good morning in korean", "hello to japanese", "translate hello to french"
        new($@"^(?:translate\s+)?(?<text>.+?)\s+(?:in|to|into)\s+(?<lang>{Lang})\s*$", Opts, Timeout),
        // "번역 hello", "translate: hello"
        new(@"^(?:번역|translate)\s*[:：]?\s+(?<text>.+)$", Opts, Timeout),
        // "hello 번역", "hello 번역해줘", "hello translate". The space matters: "자동번역" is a search.
        new(@"^(?<text>.+?)\s+(?:번역(?:해\s*줘|해|하기)?|translate)\s*$", Opts, Timeout),
        // "serendipity 뜻", "serendipity meaning", "serendipity 뜻이 뭐야"
        new(@"^(?<text>.+?)\s+(?:뜻|의미|meaning)(?:이\s*뭐야|은|이)?\s*\??\s*$", Opts, Timeout),
    ];

    public static TranslationRequest? Parse(string query)
    {
        var q = query.Trim();
        if (q.Length < 2 || q.Length > MaxLength) return null;
        foreach (var p in Patterns)
        {
            Match m;
            try { m = p.Match(q); }
            catch (RegexMatchTimeoutException) { return null; }
            if (!m.Success) continue;
            var text = m.Groups["text"].Value.Trim().Trim('"', '“', '”', '\'');
            if (text.Length == 0) continue;
            string? target = m.Groups["lang"].Success ? Languages.CodeFor(m.Groups["lang"].Value) : null;
            return new TranslationRequest(text, target);
        }
        return null;
    }

    /// <summary>
    /// Default target: the system language, unless the text already is in it, then <paramref name="secondary"/>
    /// (English by default). Scripts decide what can be decided locally; Latin text goes to the system language.
    /// </summary>
    public static string DefaultTarget(string text, string system, string secondary)
    {
        var guess = Languages.GuessByScript(text);
        if (guess == system) return secondary == system ? "en" : secondary;
        return system;
    }
}
