using System.Globalization;
using System.Text.RegularExpressions;
using Quicklight.Core.Calc;
using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>"5km in mile", "70kg lb", "30c f", "84㎡ 평": unit conversion. With no target unit, the usual counterparts are shown. Enter copies the number.</summary>
public sealed partial class UnitProvider : IResultProvider
{
    public string Name => "unit";

    /// <param name="Factor">Size in the category's base unit (m, g, byte, ㎡, L, m/s); temperatures convert through °C instead.</param>
    sealed record Unit(string Symbol, string Category, double Factor, params string[] Aliases);

    static readonly Unit[] Units =
    [
        new("mm", "length", 0.001, "mm", "밀리미터"),
        new("cm", "length", 0.01, "cm", "센티미터", "센티"),
        new("m", "length", 1, "m", "미터"),
        new("km", "length", 1000, "km", "킬로미터"),
        new("in", "length", 0.0254, "in", "inch", "inches", "인치"),
        new("ft", "length", 0.3048, "ft", "feet", "foot", "피트"),
        new("yd", "length", 0.9144, "yd", "yard", "yards", "야드"),
        new("mile", "length", 1609.344, "mi", "mile", "miles", "마일"),

        new("mg", "mass", 0.001, "mg", "밀리그램"),
        new("g", "mass", 1, "g", "그램"),
        new("kg", "mass", 1000, "kg", "킬로그램", "킬로"),
        new("t", "mass", 1_000_000, "t", "ton", "톤"),
        new("oz", "mass", 28.349523125, "oz", "ounce", "온스"),
        new("lb", "mass", 453.59237, "lb", "lbs", "pound", "pounds", "파운드"),
        new("근", "mass", 600, "근"),

        new("°C", "temp", 1, "c", "°c", "℃", "celsius", "섭씨"),
        new("°F", "temp", 1, "f", "°f", "℉", "fahrenheit", "화씨"),
        new("K", "temp", 1, "kelvin", "켈빈"),

        new("B", "data", 1, "b", "byte", "bytes", "바이트"),
        new("KB", "data", 1024, "kb", "킬로바이트"),
        new("MB", "data", 1024d * 1024, "mb", "메가", "메가바이트"),
        new("GB", "data", 1024d * 1024 * 1024, "gb", "기가", "기가바이트"),
        new("TB", "data", 1024d * 1024 * 1024 * 1024, "tb", "테라", "테라바이트"),

        new("㎡", "area", 1, "m2", "㎡", "m²", "제곱미터"),
        new("평", "area", 400.0 / 121, "평"),
        new("ft²", "area", 0.09290304, "ft2", "ft²", "sqft"),
        new("acre", "area", 4046.8564224, "acre", "acres", "에이커"),
        new("ha", "area", 10_000, "ha", "헥타르"),
        new("㎢", "area", 1_000_000, "km2", "㎢", "km²"),

        new("mL", "volume", 0.001, "ml", "밀리리터"),
        new("L", "volume", 1, "l", "리터"),
        new("gal", "volume", 3.785411784, "gal", "gallon", "gallons", "갤런"),
        new("cup", "volume", 0.2365882365, "cup", "cups", "컵"),

        new("m/s", "speed", 1, "m/s", "mps"),
        new("km/h", "speed", 1 / 3.6, "km/h", "kmh", "kph"),
        new("mph", "speed", 0.44704, "mph"),
        new("knot", "speed", 0.514444, "kn", "knot", "knots", "노트"),
    ];

    static readonly Dictionary<string, Unit> ByAlias =
        Units.SelectMany(u => u.Aliases.Select(a => (a, u))).ToDictionary(x => x.a, x => x.u, StringComparer.OrdinalIgnoreCase);

    /// <summary>Targets shown when none is typed.</summary>
    static readonly Dictionary<string, string[]> Counterparts = new()
    {
        ["mm"] = ["in"], ["cm"] = ["in"], ["m"] = ["ft"], ["km"] = ["mile"], ["in"] = ["cm"], ["ft"] = ["m"], ["yd"] = ["m"], ["mile"] = ["km"],
        ["mg"] = ["g"], ["g"] = ["oz"], ["kg"] = ["lb"], ["t"] = ["lb"], ["oz"] = ["g"], ["lb"] = ["kg"], ["근"] = ["g", "kg"],
        ["°C"] = ["°F"], ["°F"] = ["°C"], ["K"] = ["°C"],
        ["B"] = ["KB"], ["KB"] = ["MB"], ["MB"] = ["GB", "KB"], ["GB"] = ["MB", "TB"], ["TB"] = ["GB"],
        ["㎡"] = ["평"], ["평"] = ["㎡"], ["ft²"] = ["㎡"], ["acre"] = ["㎡", "평"], ["ha"] = ["㎡", "평"], ["㎢"] = ["ha"],
        ["mL"] = ["cup"], ["L"] = ["gal"], ["gal"] = ["L"], ["cup"] = ["mL"],
        ["m/s"] = ["km/h"], ["km/h"] = ["mph"], ["mph"] = ["km/h"], ["knot"] = ["km/h"],
    };

    // Units never start with a digit, so "10 km" cannot split into the number 1 and a unit "0".
    [GeneratedRegex(@"^(-?[\d,]*\.?\d+)\s*([^\s\d]\S*?)(?:\s*(?:\bin\b|\bto\b|->|→|=)\s*|\s+)([^\s\d]\S*)$|^(-?[\d,]*\.?\d+)\s*([^\s\d]\S*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct) =>
        Task.FromResult(query.IsAlternate ? [] : Convert(query.Text));

    /// <summary>Conversion rows for "5km in mile" (one) or "5km" (the usual counterparts); empty if the text is not a conversion.</summary>
    public static IReadOnlyList<SearchResult> Convert(string text)
    {
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return [];
        bool hasTarget = m.Groups[1].Success;
        var number = hasTarget ? m.Groups[1].Value : m.Groups[4].Value;
        var fromAlias = hasTarget ? m.Groups[2].Value : m.Groups[5].Value;
        if (!double.TryParse(number.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return [];
        if (!ByAlias.TryGetValue(fromAlias, out var from)) return [];

        IEnumerable<Unit> targets;
        if (hasTarget)
        {
            if (!ByAlias.TryGetValue(m.Groups[3].Value, out var to) || to.Category != from.Category || to == from) return [];
            targets = [to];
        }
        else targets = Counterparts[from.Symbol].Select(s => Units.First(u => u.Symbol == s));

        var results = new List<SearchResult>();
        int i = 0;
        foreach (var to in targets)
        {
            double value = Math.Round(ConvertValue(amount, from, to), 6);
            results.Add(new SearchResult
            {
                Title = $"{Calculator.Format(value)} {to.Symbol}",
                Subtitle = $"{Calculator.Format(amount)} {from.Symbol} = {Calculator.Format(value)} {to.Symbol}",
                Kind = ResultKind.Calculator,
                Target = Calculator.FormatPlain(value),
                Action = ActionType.Copy,
                Score = Scores.Unit - i++,
            });
        }
        return results;
    }

    static double ConvertValue(double v, Unit from, Unit to)
    {
        if (from.Category != "temp") return v * from.Factor / to.Factor;
        double c = from.Symbol switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
        return to.Symbol switch { "°F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
    }
}
