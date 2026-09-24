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
        // "사과 영어로", "사과 영어로 번역해줘", "사과를 영어로", "사과는 영어로 뭐야?"
        new($@"^(?<text>.+?)(?:을|를|은|는)?\s*(?<lang>{Lang})\s*(?:으로|로)(?:\s*(?:번역|바꿔)(?:해\s*줘|해|하기)?|\s*(?:뭐야|뭐지|뭐라고\s*해))?\s*\??\s*$", Opts, Timeout),
        // "영어로 번역 사과". 번역 is required: "한국어로 설정", "한글로 문서 만들기" are searches.
        new($@"^(?<lang>{Lang})\s*(?:으로|로)\s+번역(?:해\s*줘|해)?\s+(?<text>.+)$", Opts, Timeout),
        // "translate hello to french"
        new($@"^translate\s+(?<text>.+?)\s+(?:in|to|into)\s+(?<lang>{Lang})\s*$", Opts, Timeout),
        // "good morning in korean" (only "in": "change language to korean" is a search)
        new($@"^(?<text>.+?)\s+in\s+(?<lang>{Lang})\s*\??\s*$", Opts, Timeout),
        // "번역 hello", "translate: hello"
        new(@"^(?:번역|translate)\s*[:：]?\s+(?<text>.+)$", Opts, Timeout),
        // "hello 번역", "hello 번역해줘", "hello translate". The space matters: "자동번역" is a search.
        new(@"^(?<text>.+?)\s+(?:번역(?:해\s*줘|해|하기)?|translate)\s*$", Opts, Timeout),
        // "serendipity 뜻", "serendipity의 뜻", "serendipity meaning", "serendipity 뜻이 뭐야". Not 의미: "삶의 의미" is a search.
        new(@"^(?<text>.+?)(?:의)?\s+(?:뜻|meaning)(?:이\s*뭐야|은\s*뭐야|이|은)?\s*\??\s*$", Opts, Timeout),
    ];

    static readonly Regex OnlyLanguage = new($@"^(?:{Lang})\s*(?:으로|로)?$", Opts, Timeout);

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
            if (text.Length == 0 || OnlyLanguage.IsMatch(text)) continue; // "영어로 번역" has nothing to translate
            string? target = m.Groups["lang"].Success ? Languages.CodeFor(m.Groups["lang"].Value) : null;
            return new TranslationRequest(text, target);
        }
        return null;
    }

    /// <summary>
    /// Default target: the system language, unless the text already is in it, then <paramref name="secondary"/>
    /// (English by default). Scripts decide what can be decided locally; Latin text goes to the system language.
    /// </summary>
    public static string DefaultTarget(string text, string system, string secondary, IReadOnlyCollection<string>? models = null)
    {
        // A system language the server has no model for (e.g. German with ko/en/ja/zh loaded) cannot be the target.
        if (models is { Count: > 0 } && !models.Contains(system)) system = models.Contains(secondary) ? secondary : "en";
        var guess = Languages.GuessByScript(text);
        if (guess == system) return Fallback(system, secondary, models);
        return system;
    }

    /// <summary>
    /// Where to go instead when the text already is in <paramref name="target"/> (the server detected it so):
    /// the secondary language, or else another loaded language.
    /// </summary>
    public static string Fallback(string target, string secondary, IReadOnlyCollection<string>? models = null)
    {
        if (secondary != target) return secondary;
        if (target != "en") return "en";
        return models?.FirstOrDefault(m => m != target) ?? "ko";
    }
}
