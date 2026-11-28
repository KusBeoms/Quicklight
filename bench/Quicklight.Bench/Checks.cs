using System.Text.RegularExpressions;

namespace Quicklight.Bench;

/// <summary>A tool call Qutra made (recorded, never run unless it only reads).</summary>
sealed record Call(string Name, JsonObject Args);

/// <summary>
/// Rule-based verdicts on an answer (no model judges it, so a score only moves when the answer does). Every key in a
/// case's "checks" object must hold; the first that does not is the reason.
/// </summary>
static class Checks
{
    public static string? Fail(JsonObject? checks, string answer, IReadOnlyList<Call> calls, IReadOnlyList<string> asked)
    {
        if (checks is null) return null;
        var norm = Text.Norm(answer);
        foreach (var (key, value) in checks)
        {
            if (value is null) continue;
            string? why = key switch
            {
                "has" => Strings(value).FirstOrDefault(s => !norm.Contains(Text.Norm(Text.Expand(s)))) is { } miss ? $"'{miss}' 없음" : null,
                "any" => Strings(value).Any(s => norm.Contains(Text.Norm(Text.Expand(s)))) ? null : $"{string.Join("/", Strings(value))} 중 하나도 없음",
                "not" => Strings(value).FirstOrDefault(s => norm.Contains(Text.Norm(Text.Expand(s)))) is { } hit ? $"'{hit}' 있음" : null,
                "regex" => Regex.IsMatch(answer, value.GetValue<string>(), RegexOptions.IgnoreCase) ? null : $"/{value}/ 안 맞음",
                "notRegex" => Regex.IsMatch(answer, value.GetValue<string>(), RegexOptions.IgnoreCase) ? $"/{value}/ 맞음" : null,
                "lang" => Lang(answer, value.GetValue<string>()),
                "bullets" => Range(answer.Split('\n').Count(l => l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("• ")), value, "불릿"),
                "lines" => Range(answer.Split('\n').Count(l => l.Trim().Length > 0), value, "줄"),
                "oneLine" => answer.Trim().Contains('\n') ? "여러 줄" : null,
                "noBackticks" => answer.Contains('`') ? "백틱 있음" : null,
                "maxChars" => answer.Length > value.GetValue<int>() ? $"{answer.Length}자 (최대 {value})" : null,
                "tool" => Each(value).Select(t => Called(t, calls)).FirstOrDefault(w => w is not null),
                "toolAny" => Each(value).Any(t => Called(t, calls) is null) ? null : "기대한 도구 호출 없음: " + Names(calls),
                "firstTool" => calls.FirstOrDefault()?.Name == value.GetValue<string>() ? null : $"첫 도구가 {calls.FirstOrDefault()?.Name ?? "없음"}",
                "noTool" => Strings(value).FirstOrDefault(n => calls.Any(c => c.Name == n)) is { } bad ? $"{bad} 호출함" : null,
                "noTools" => calls.Count > 0 ? "도구 호출함: " + Names(calls) : null,
                "asked" => (asked.Count > 0) == value.GetValue<bool>() ? null : asked.Count > 0 ? "확인을 물음" : "확인을 묻지 않음",
                _ => $"모르는 검사 '{key}'",
            };
            if (why is not null) return why;
        }
        return null;
    }

    static IEnumerable<string> Strings(JsonNode n) => n is JsonArray a ? a.Select(x => x!.GetValue<string>()) : [n.GetValue<string>()];
    static IEnumerable<JsonObject> Each(JsonNode n) => n is JsonArray a ? a.OfType<JsonObject>() : [n.AsObject()];
    static string Names(IReadOnlyList<Call> calls) => calls.Count == 0 ? "없음" : string.Join(", ", calls.Select(c => c.Name));

    static string? Range(int n, JsonNode range, string what)
    {
        var r = range.AsArray();
        int lo = r[0]!.GetValue<int>(), hi = r[1]!.GetValue<int>();
        return n >= lo && n <= hi ? null : $"{what} {n}개 ({lo}~{hi})";
    }

    /// <summary>ko: mostly Hangul; en: no Hangul/kana/CJK; ja: has kana; zh: CJK without kana or Hangul.</summary>
    static string? Lang(string s, string want)
    {
        int hangul = s.Count(c => c is >= '가' and <= '힣'), kana = s.Count(c => c is >= '぀' and <= 'ヿ'), cjk = s.Count(c => c is >= '一' and <= '鿿');
        int letters = s.Count(char.IsLetter);
        bool ok = want switch
        {
            "ko" => letters > 0 && hangul >= letters * 0.3,
            "en" => hangul == 0 && kana == 0 && cjk == 0 && letters > 0,
            "ja" => kana > 0,
            "zh" => cjk > 0 && kana == 0 && hangul == 0,
            _ => false,
        };
        return ok ? null : $"언어가 {want} 아님";
    }

    /// <summary>A call with this name whose arguments contain the expected ones (numbers equal, "&lt;0"/"&gt;0", strings contained).</summary>
    static string? Called(JsonObject want, IReadOnlyList<Call> calls)
    {
        var name = want["name"]!.GetValue<string>();
        var args = want["args"] as JsonObject;
        var same = calls.Where(c => c.Name == name).ToList();
        if (same.Count == 0) return $"{name} 호출 없음 (호출: {Names(calls)})";
        if (args is null || same.Any(c => ArgsMatch(args, c.Args))) return null;
        return $"{name} 인자 다름: {same[^1].Args.ToJsonString()}";
    }

    static bool ArgsMatch(JsonObject want, JsonObject got)
    {
        foreach (var (k, v) in want)
        {
            var g = got[k];
            if (v is null) continue;
            if (g is null) return false;
            var gs = g is JsonValue gv && gv.TryGetValue<string>(out var str) ? str : g.ToJsonString();
            if (v is JsonValue jv && jv.TryGetValue<double>(out var num))
            {
                if (!double.TryParse(gs, System.Globalization.CultureInfo.InvariantCulture, out var gn) || Math.Abs(gn - num) > 1e-6) return false;
            }
            else
            {
                var ws = Text.Expand(v.GetValue<string>());
                if (ws is ['<' or '>', ..] && double.TryParse(ws[1..], System.Globalization.CultureInfo.InvariantCulture, out var bound))
                {
                    if (!double.TryParse(gs, System.Globalization.CultureInfo.InvariantCulture, out var gn) || (ws[0] == '<' ? gn >= bound : gn <= bound)) return false;
                }
                else if (!Text.Norm(gs).Contains(Text.Norm(ws))) return false;
            }
        }
        return true;
    }
}
