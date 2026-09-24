using System.Globalization;

namespace Quicklight.Core.Translation;

/// <summary>Language names people type (Korean and English) mapped to LibreTranslate codes, plus a script-based guess.</summary>
public static class Languages
{
    static readonly (string Code, string Korean, string[] Names)[] Table =
    [
        ("ko", "한국어", ["한국어", "한국말", "한글", "korean"]),
        ("en", "영어", ["영어", "english"]),
        ("ja", "일본어", ["일본어", "일어", "japanese"]),
        ("zh", "중국어", ["중국어", "중국말", "chinese"]),
        ("es", "스페인어", ["스페인어", "spanish"]),
        ("fr", "프랑스어", ["프랑스어", "불어", "french"]),
        ("de", "독일어", ["독일어", "german"]),
        ("ru", "러시아어", ["러시아어", "russian"]),
        ("vi", "베트남어", ["베트남어", "vietnamese"]),
        ("th", "태국어", ["태국어", "thai"]),
        ("id", "인도네시아어", ["인도네시아어", "indonesian"]),
        ("it", "이탈리아어", ["이탈리아어", "italian"]),
        ("pt", "포르투갈어", ["포르투갈어", "portuguese"]),
        ("ar", "아랍어", ["아랍어", "arabic"]),
        ("hi", "힌디어", ["힌디어", "hindi"]),
        ("tr", "튀르키예어", ["튀르키예어", "터키어", "turkish"]),
    ];

    static readonly Dictionary<string, string> ByName = Table
        .SelectMany(l => l.Names.Select(n => (n, l.Code)))
        .ToDictionary(x => x.n, x => x.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>All typed names, longest first (for building patterns).</summary>
    public static IEnumerable<string> AllNames => ByName.Keys.OrderByDescending(k => k.Length);

    public static string? CodeFor(string name) => ByName.TryGetValue(name.Trim(), out var c) ? c : null;

    public static string KoreanName(string code)
    {
        // The server answers with script variants such as "zh-Hans"; the base language names them.
        var baseCode = code.Split('-')[0].ToLowerInvariant();
        return Table.FirstOrDefault(l => l.Code == baseCode).Korean ?? code;
    }

    /// <summary>The Windows display language as a two-letter code ("ko" on a Korean system).</summary>
    public static string System => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is { Length: 2 } c ? c : "en";

    /// <summary>
    /// Guesses the language from its script. Only scripts that identify one language are used
    /// (Hangul → ko, kana → ja, Thai → th); Latin text returns null and is left to the server's detection.
    /// </summary>
    public static string? GuessByScript(string text)
    {
        int hangul = 0, kana = 0, han = 0, thai = 0, cyrillic = 0, letters = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (ch is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㆎ') hangul++;
            else if (ch is >= '぀' and <= 'ヿ') kana++;
            else if (ch is >= '一' and <= '鿿') han++;
            else if (ch is >= '฀' and <= '๿') thai++;
            else if (ch is >= 'Ѐ' and <= 'ӿ') cyrillic++;
        }
        if (letters == 0) return null;
        if (hangul * 2 >= letters) return "ko";
        if (kana > 0 && (kana + han) * 2 >= letters) return "ja";
        if (han * 2 >= letters) return "zh";
        if (thai * 2 >= letters) return "th";
        if (cyrillic * 2 >= letters) return "ru";
        return null;
    }
}
