using System.Globalization;
using System.Text.RegularExpressions;
using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>
/// Date and time answers: "오늘 +100일" / "today +30d", "d-day 2026-12-25" / "2026-12-25까지", "도쿄 시간" / "now tokyo".
/// Enter copies the answer.
/// </summary>
public sealed partial class DateProvider(Func<DateTime>? clock = null) : IResultProvider
{
    public string Name => "date";

    static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    static readonly Dictionary<string, string> Cities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["서울"] = "Korea Standard Time", ["seoul"] = "Korea Standard Time",
        ["도쿄"] = "Tokyo Standard Time", ["tokyo"] = "Tokyo Standard Time",
        ["베이징"] = "China Standard Time", ["beijing"] = "China Standard Time", ["상하이"] = "China Standard Time", ["shanghai"] = "China Standard Time",
        ["홍콩"] = "China Standard Time", ["hongkong"] = "China Standard Time", ["타이베이"] = "Taipei Standard Time", ["taipei"] = "Taipei Standard Time",
        ["싱가포르"] = "Singapore Standard Time", ["singapore"] = "Singapore Standard Time",
        ["방콕"] = "SE Asia Standard Time", ["bangkok"] = "SE Asia Standard Time", ["하노이"] = "SE Asia Standard Time", ["hanoi"] = "SE Asia Standard Time",
        ["두바이"] = "Arabian Standard Time", ["dubai"] = "Arabian Standard Time",
        ["런던"] = "GMT Standard Time", ["london"] = "GMT Standard Time",
        ["파리"] = "Romance Standard Time", ["paris"] = "Romance Standard Time",
        ["베를린"] = "W. Europe Standard Time", ["berlin"] = "W. Europe Standard Time",
        ["뉴욕"] = "Eastern Standard Time", ["newyork"] = "Eastern Standard Time", ["ny"] = "Eastern Standard Time",
        ["시카고"] = "Central Standard Time", ["chicago"] = "Central Standard Time",
        ["la"] = "Pacific Standard Time", ["로스앤젤레스"] = "Pacific Standard Time", ["샌프란시스코"] = "Pacific Standard Time",
        ["sf"] = "Pacific Standard Time", ["시애틀"] = "Pacific Standard Time", ["seattle"] = "Pacific Standard Time",
        ["하와이"] = "Hawaiian Standard Time", ["hawaii"] = "Hawaiian Standard Time",
        ["시드니"] = "AUS Eastern Standard Time", ["sydney"] = "AUS Eastern Standard Time",
        ["utc"] = "UTC",
    };

    [GeneratedRegex(@"^(?:today|오늘|now|지금)\s*([+-])\s*(\d+)\s*(d|days?|일|w|weeks?|주|m|months?|개월|달|y|years?|년)(?:\s*(?:후|뒤|전))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();

    [GeneratedRegex(@"^(?:d-?day|디데이)\s+(.+)$|^(.+?)\s*까지$", RegexOptions.IgnoreCase)]
    private static partial Regex DDay();

    [GeneratedRegex(@"^(?:(?:now|지금)\s+(\S+)|(\S+)\s*(?:time|시간|시각))$", RegexOptions.IgnoreCase)]
    private static partial Regex CityTime();

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct) =>
        Task.FromResult(query.IsAlternate ? [] : Evaluate(query.Text, (clock ?? (() => DateTime.Now))()));

    public static IReadOnlyList<SearchResult> Evaluate(string text, DateTime now)
    {
        text = text.Trim();
        var today = now.Date;

        if (Offset().Match(text) is { Success: true } o)
        {
            int n = int.Parse(o.Groups[2].Value) * (o.Groups[1].Value == "-" ? -1 : 1);
            var unit = o.Groups[3].Value.ToLowerInvariant();
            var date = unit[0] switch
            {
                'w' or '주' => today.AddDays(7 * n),
                'm' or '개' or '달' => today.AddMonths(n),
                'y' or '년' => today.AddYears(n),
                _ => today.AddDays(n),
            };
            return [Answer(LongDate(date), $"오늘({LongDate(today)}) {o.Groups[1].Value}{Math.Abs(n)}{o.Groups[3].Value}", date.ToString("yyyy-MM-dd"))];
        }

        if (DDay().Match(text) is { Success: true } d && ParseDate((d.Groups[1].Success ? d.Groups[1] : d.Groups[2]).Value, today) is { } target)
        {
            int days = (target - today).Days;
            var label = days == 0 ? "D-Day" : days > 0 ? $"D-{days}" : $"D+{-days}";
            return [Answer(label, $"{LongDate(target)}까지 {Math.Abs(days):#,0}일 {(days >= 0 ? "남음" : "지남")}", label)];
        }

        if (CityTime().Match(text) is { Success: true } c
            && Cities.TryGetValue((c.Groups[1].Success ? c.Groups[1] : c.Groups[2]).Value, out var zoneId))
        {
            var city = (c.Groups[1].Success ? c.Groups[1] : c.Groups[2]).Value;
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
            catch (TimeZoneNotFoundException) { return []; }
            var there = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local, zone);
            var diff = zone.GetUtcOffset(now.ToUniversalTime()) - TimeZoneInfo.Local.GetUtcOffset(now);
            var diffText = diff == TimeSpan.Zero ? "같은 시간" : $"{(diff > TimeSpan.Zero ? "+" : "-")}{diff.Duration():h\\:mm}";
            return [Answer(there.ToString("tt h:mm", Korean), $"{city} · {LongDate(there.Date)} · 여기와 {diffText}", there.ToString("HH:mm"))];
        }

        return [];
    }

    static DateTime? ParseDate(string s, DateTime today)
    {
        s = s.Trim().TrimEnd('.');
        if (Regex.IsMatch(s, @"\d{4}"))
            return DateTime.TryParseExact(s, ["yyyy-M-d", "yyyy.M.d", "yyyy/M/d", "yyyyMMdd", "yyyy년 M월 d일"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        // Without a year: the next such day. Parsed in a leap year so 2월 29일 is valid, then the next year that has it.
        if (!DateTime.TryParseExact("2000 " + s, ["yyyy M월 d일", "yyyy M/d", "yyyy M-d", "yyyy M.d"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var md)) return null;
        for (int y = today.Year; ; y++)
            if (md.Day <= DateTime.DaysInMonth(y, md.Month) && new DateTime(y, md.Month, md.Day) is var next && next >= today) return next;
    }

    static string LongDate(DateTime d) => d.ToString("yyyy년 M월 d일 (ddd)", Korean);

    static SearchResult Answer(string title, string subtitle, string copy) => new()
    {
        Title = title,
        Subtitle = subtitle,
        Kind = ResultKind.Calculator,
        Target = copy,
        Action = ActionType.Copy,
        Score = Scores.Date,
    };
}
