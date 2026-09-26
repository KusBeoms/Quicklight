using System.Globalization;
using Quicklight.Core.Matching;

namespace Quicklight.Core.Shell;

/// <summary>
/// The "all apps" grid: every installed app, cleaned up (no uninstallers, help links or duplicates),
/// sorted by name and sectioned by initial: A–Z, then ㄱ–ㅎ, then # for everything else.
/// </summary>
public static class AppCatalog
{
    /// <summary>Queries that open the grid instead of a search.</summary>
    static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "application", "applications", "program", "programs",
        "앱", "어플", "애플리케이션", "어플리케이션", "응용프로그램", "응용 프로그램", "프로그램",
    };

    public static bool IsCatalogQuery(string query) => Keywords.Contains(query.Trim());

    /// <summary>
    /// "app apple music", "app: apple music", "앱; 멜론", "app, code": a search among the apps only. Returns what follows
    /// the keyword and its separator (space, colon, semicolon or comma), or null for any other query.
    /// </summary>
    public static string? ScopedQuery(string query)
    {
        var q = query.Trim();
        foreach (var k in Keywords.OrderByDescending(k => k.Length))
        {
            if (q.Length <= k.Length || !q.StartsWith(k, StringComparison.OrdinalIgnoreCase)) continue;
            if (!(char.IsWhiteSpace(q[k.Length]) || q[k.Length] is ':' or ';' or ',')) continue; // "apple" is not "app le"
            var rest = q[k.Length..].TrimStart(':', ';', ',', ' ', '\t').Trim();
            if (rest.Length > 0) return rest;
        }
        return null;
    }

    public sealed record Section(string Name, IReadOnlyList<AppEntry> Apps);

    public static IReadOnlyList<Section> Build(IEnumerable<AppEntry> apps)
    {
        var compare = StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), CompareOptions.IgnoreCase);
        return apps
            .Where(a => !IsNoise(a.Name))
            .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()) // same name twice: keep one
            .GroupBy(a => SectionOf(a.Name))
            .OrderBy(g => SectionOrder(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Section(g.Key, g.OrderBy(a => a.Name, compare).ToList()))
            .ToList();
    }

    static bool IsNoise(string name) => ShellApps.RoleOf(name) is ShellApps.Role.Noise or ShellApps.Role.Uninstaller;

    /// <summary>"Chrome" → "C", "카카오톡" → "ㅋ", "까치" → "ㄱ" (double consonants share the plain one), "7-Zip" → "#".</summary>
    public static string SectionOf(string name)
    {
        var c = name.TrimStart().FirstOrDefault();
        if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return char.ToUpperInvariant(c).ToString();
        if (Hangul.IsSyllable(c) || Hangul.IsConsonantJamo(c))
        {
            var cho = Hangul.ToChoseong(c.ToString())[0];
            return (cho switch { 'ㄲ' => 'ㄱ', 'ㄸ' => 'ㄷ', 'ㅃ' => 'ㅂ', 'ㅆ' => 'ㅅ', 'ㅉ' => 'ㅈ', _ => cho }).ToString();
        }
        return "#";
    }

    static int SectionOrder(string section) => section switch
    {
        "#" => 2,
        _ when section[0] is >= 'A' and <= 'Z' => 0,
        _ => 1,
    };
}
