using System.Globalization;

namespace Quicklight.Core.Currency;

/// <summary>Names people type for currencies (Korean and English), display symbols and decimal places.</summary>
public static class Currencies
{
    /// <summary>Lower-case alias → ISO 4217 code. ISO codes themselves are accepted separately.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = Build(new()
    {
        ["USD"] = ["$", "us$", "dollar", "dollars", "달러", "불", "미국달러", "미국 달러", "미화", "달라"],
        ["KRW"] = ["₩", "won", "원", "원화", "한화"],
        ["JPY"] = ["¥", "円", "yen", "엔", "엔화", "일본엔"],
        ["EUR"] = ["€", "euro", "euros", "유로", "유로화"],
        ["CNY"] = ["元", "rmb", "yuan", "위안", "위안화", "인민폐", "중국위안"],
        ["GBP"] = ["£", "pound", "pounds", "파운드", "영국파운드"],
        ["HKD"] = ["hk$", "홍콩달러", "홍콩 달러"],
        ["TWD"] = ["nt$", "대만달러", "대만 달러"],
        ["SGD"] = ["s$", "싱가포르달러", "싱가포르 달러", "싱달러"],
        ["AUD"] = ["a$", "호주달러", "호주 달러"],
        ["CAD"] = ["c$", "캐나다달러", "캐나다 달러"],
        ["NZD"] = ["nz$", "뉴질랜드달러", "뉴질랜드 달러"],
        ["CHF"] = ["스위스프랑", "스위스 프랑", "프랑"],
        ["THB"] = ["฿", "baht", "바트"],
        // Not "동"/"dong" alone: "101동" is an apartment building, not Vietnamese money.
        ["VND"] = ["₫", "베트남동", "베트남 동"],
        ["PHP"] = ["₱", "peso", "pesos", "페소"],
        ["INR"] = ["₹", "rupee", "rupees", "루피"],
        ["IDR"] = ["rupiah", "루피아"],
        ["MYR"] = ["ringgit", "링깃"],
        ["RUB"] = ["₽", "ruble", "rubles", "루블"],
        ["MXN"] = ["멕시코페소", "멕시코 페소"],
        ["TRY"] = ["₺", "lira", "리라"],
        ["AED"] = ["dirham", "디르함"],
    });

    static Dictionary<string, string> Build(Dictionary<string, string[]> byCode)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, names) in byCode)
            foreach (var n in names) map[n] = code;
        return map;
    }

    static readonly Dictionary<string, string> Symbols = new()
    {
        ["USD"] = "$", ["KRW"] = "₩", ["JPY"] = "¥", ["EUR"] = "€", ["GBP"] = "£",
    };

    static readonly Dictionary<string, string> KoreanNames = new()
    {
        ["USD"] = "미국 달러", ["KRW"] = "원", ["JPY"] = "일본 엔", ["EUR"] = "유로", ["CNY"] = "중국 위안", ["GBP"] = "영국 파운드",
        ["HKD"] = "홍콩 달러", ["TWD"] = "대만 달러", ["SGD"] = "싱가포르 달러", ["AUD"] = "호주 달러", ["CAD"] = "캐나다 달러",
        ["NZD"] = "뉴질랜드 달러", ["CHF"] = "스위스 프랑", ["THB"] = "태국 바트", ["VND"] = "베트남 동", ["PHP"] = "필리핀 페소",
        ["INR"] = "인도 루피", ["IDR"] = "인도네시아 루피아", ["MYR"] = "말레이시아 링깃", ["RUB"] = "러시아 루블",
        ["MXN"] = "멕시코 페소", ["TRY"] = "튀르키예 리라", ["AED"] = "UAE 디르함",
    };

    /// <summary>Currencies usually quoted without minor units.</summary>
    static readonly HashSet<string> NoDecimals = ["KRW", "JPY", "VND", "IDR", "CLP", "ISK", "HUF", "TWD", "PYG", "UGX"];

    public static int Decimals(string code) => NoDecimals.Contains(code) ? 0 : 2;

    public static string KoreanName(string code) => KoreanNames.TryGetValue(code, out var n) ? n : code;

    /// <summary>Resolves an alias or ISO code to a code, or null. ISO codes must exist in <paramref name="isKnown"/>.</summary>
    public static string? Resolve(string token, Func<string, bool> isKnown)
    {
        var t = token.Trim();
        if (Aliases.TryGetValue(t, out var code)) return isKnown(code) ? code : null;
        if (t.Length == 3 && t.All(char.IsAsciiLetter) && isKnown(t.ToUpperInvariant())) return t.ToUpperInvariant();
        return null;
    }

    /// <summary>Number with grouping and the currency's usual decimals: 1,374.5 → "1,374.50", KRW → "137,450".</summary>
    public static string FormatNumber(double value, string code, bool grouping = true)
    {
        int d = Decimals(code), extra = 0;
        // Tiny amounts (e.g. 1 KRW in USD) need more digits to say anything; shown only as far as needed.
        if (value != 0 && Math.Abs(value) < 1) extra = Math.Max(0, Math.Min(12, 2 - (int)Math.Floor(Math.Log10(Math.Abs(value)))) - d);
        var rounded = Math.Round(value, d + extra) + 0.0;
        var format = (grouping ? "#,0" : "0") + (d + extra > 0 ? "." + new string('0', d) + new string('#', extra) : "");
        return rounded.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>An exchange rate keeps precision even for currencies without minor units: 1,364.77, 0.1158, 0.000714.</summary>
    public static string FormatRate(double rate)
    {
        if (!(rate > 0) || !double.IsFinite(rate)) return "—";
        int d = rate >= 100 ? 2 : rate >= 1 ? 4 : Math.Min(8, 3 - (int)Math.Floor(Math.Log10(rate)));
        return (Math.Round(rate, d) + 0.0).ToString("#,0." + new string('#', d), CultureInfo.InvariantCulture);
    }

    /// <summary>"$1,374.50", "₩137,450", "1,234.56 CHF".</summary>
    public static string Format(double value, string code)
    {
        if (!Symbols.TryGetValue(code, out var sym)) return $"{FormatNumber(value, code)} {code}";
        var digits = FormatNumber(Math.Abs(value), code);
        // The sign is decided after rounding, so -0.001 KRW reads ₩0, not -₩0.
        bool negative = value < 0 && digits.Any(c => c is >= '1' and <= '9');
        return (negative ? "-" : "") + sym + digits;
    }
}
