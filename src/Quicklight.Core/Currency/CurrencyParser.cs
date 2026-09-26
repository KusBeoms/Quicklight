using System.Globalization;
using System.Text.RegularExpressions;

namespace Quicklight.Core.Currency;

public sealed record CurrencyQuery(double Amount, string From, string? To);

/// <summary>
/// Recognizes conversion requests. The whole query must match, so ordinary searches are never taken over:
/// "100달러", "$100", "100 usd to krw", "5만원 엔", "100달러를 유로로", "1.2억 원 usd", "100달러는 몇 원?".
/// </summary>
public static class CurrencyParser
{
    const string Number = @"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?|\.\d+";

    // Longest aliases first so "홍콩달러" wins over "달러" and "루피아" over "루피".
    static readonly string Names = string.Join("|", Currencies.Aliases.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
    static readonly string AliasPattern = Names + "|[a-z]{3}";

    // A bare 3-letter code only counts after the number ("100 php"), never before it: "php 8", "top 10", "all 3" are searches.
    // "5", "5백만", "1억 2천만", "1.2억": numbers with Korean units, possibly several groups.
    static readonly string Amount = $@"(?:{Number})(?:\s*[십백천만억])*(?:\s*(?:{Number})(?:\s*[십백천만억])+)*(?:\s*(?:{Number}))?";

    static readonly Regex Pattern = new(
        $@"^\s*(?:(?<src>{Names})\s*(?<amt>{Amount})|(?<amt>{Amount})\s*(?<src>{AliasPattern}))" +
        @"\s*(?:을|를|은|는|이|가|이면|면)?" +
        $@"(?:\s*(?:to|in|into|->|→|=>|=|에서|은|는)?\s*(?:몇\s*)?(?<dst>{AliasPattern})\s*(?:으로|로)?)?" +
        @"(?:\s*(?:환전|환산|변환|바꾸기|얼마|이야|야|\?))*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        // Worst measured input takes ~1.5 ms. The cap is only a safety net, and generous because the first match
        // after startup also pays for JIT compilation of the compiled regex.
        TimeSpan.FromMilliseconds(500));

    static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
        { "all", "top", "try", "cup", "mad", "sos", "bob", "gel", "mop", "bam", "gip", "lak", "mga", "kes", "pen", "kid", "imp" };

    static bool IsCommonWord(string token) => CommonWords.Contains(token) && token != token.ToUpperInvariant();

    static readonly Regex NumberOrUnit = new($@"{Number}|[십백천만억]", RegexOptions.Compiled);

    /// <summary>
    /// Reads Arabic numbers with Korean units the way Koreans say them: 십/백/천 build a group, 만/억 close it.
    /// "5백만" = 5,000,000, "1억 2천만" = 120,000,000, "5만천" = 51,000, "1.2억" = 120,000,000. Null if malformed ("5만만").
    /// </summary>
    internal static double? KoreanNumber(string text)
    {
        double total = 0, group = 0, current = 0;
        bool haveCurrent = false;
        double lastBig = double.MaxValue, lastSmall = double.MaxValue;
        foreach (Match t in NumberOrUnit.Matches(text))
        {
            var s = t.Value;
            if (char.IsDigit(s[0]) || s[0] == '.')
            {
                if (haveCurrent) return null; // two numbers in a row
                if (!double.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out current)) return null;
                haveCurrent = true;
                continue;
            }
            double unit = s[0] switch { '십' => 10, '백' => 100, '천' => 1_000, '만' => 10_000, _ => 100_000_000 };
            if (unit < 10_000)
            {
                if (unit >= lastSmall) return null; // "5천천", "3백천": units must shrink within a group
                group += (haveCurrent ? current : 1) * unit; // "천" alone means one thousand
                lastSmall = unit;
            }
            else
            {
                if (unit >= lastBig) return null; // 만 after 만, 억 after 만: not a number
                group += haveCurrent ? current : 0;
                if (group == 0) group = 1;
                total += group * unit;
                group = 0;
                lastBig = unit;
                lastSmall = double.MaxValue;
            }
            current = 0;
            haveCurrent = false;
        }
        return total + group + (haveCurrent ? current : 0);
    }

    static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // A run of Korean number syllables: "오백", "천오백", "삼만", "이십오". Digits only count inside a run that has a unit,
    // so "이" in "이야" or "일" in "일본" stay words.
    static readonly Regex KoreanNumeral = new("[일이삼사오육칠팔구십백천만억]+", RegexOptions.Compiled);

    /// <summary>"오백달러" → "5백달러", "천오백 엔" → "1천5백 엔", "이십오만원" → "2십5만원": number words to what <see cref="Amount"/> reads.</summary>
    internal static string DigitsForWords(string text) => KoreanNumeral.Replace(text, m =>
    {
        var run = m.Value;
        if (run.IndexOfAny(['십', '백', '천', '만', '억']) < 0) return run;
        var digits = string.Concat(run.Select(c => "일이삼사오육칠팔구".IndexOf(c) is var i and >= 0 ? (char)('1' + i) : c));
        // A unit first ("천오백") means one of it; after a digit ("5백" typed as 5 + 백) it continues that number.
        bool afterDigit = m.Index > 0 && (char.IsDigit(text[m.Index - 1]) || text[m.Index - 1] == '.');
        return !afterDigit && !char.IsDigit(digits[0]) ? "1" + digits : digits;
    });

    /// <param name="isKnown">Whether the rate table has an ISO code (unknown 3-letter words are not currencies).</param>
    public static CurrencyQuery? Parse(string text, Func<string, bool> isKnown)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80) return null;
        // Collapse whitespace first: runs of spaces between the optional parts are what makes backtracking slow.
        text = DigitsForWords(WhitespaceRun.Replace(text.Trim(), " "));
        Match m;
        try { m = Pattern.Match(text); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;

        var srcToken = m.Groups["src"].Value;
        // Lower-case English words that happen to be ISO codes ("3 cup", "2 mad") are searches; "3 CUP" is money.
        if (IsCommonWord(srcToken)) return null;
        var from = Currencies.Resolve(srcToken, isKnown);
        if (from is null) return null;
        string? to = null;
        if (m.Groups["dst"].Success)
        {
            if (IsCommonWord(m.Groups["dst"].Value)) return null; // "100달러 all" is a search too
            to = Currencies.Resolve(m.Groups["dst"].Value, isKnown);
            if (to is null) return null;
        }

        if (KoreanNumber(m.Groups["amt"].Value) is not { } amount || !(amount > 0) || double.IsInfinity(amount)) return null;
        return new CurrencyQuery(amount, from, to == from ? null : to);
    }
}
