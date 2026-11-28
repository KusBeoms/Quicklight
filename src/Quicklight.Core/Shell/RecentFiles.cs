using Quicklight.Core.Everything;
using Quicklight.Core.Matching;
using Quicklight.Core.Models;

namespace Quicklight.Core.Shell;

/// <summary>The "최근" scope: what Windows lists in its Recent folder, newest first, or Everything's last-7-days files when that is empty.</summary>
public static class RecentFiles
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent");

    /// <summary>Targets of the .lnk files in <paramref name="dir"/>, by the shortcut's own time, newest first. Missing and excluded targets are skipped.</summary>
    public static IReadOnlyList<SearchResult> FromShortcuts(string dir, Func<string, string?> targetOf, Func<string, bool> excluded, int limit = 60)
    {
        if (!Directory.Exists(dir)) return [];
        var list = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lnk in new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTime))
        {
            if (list.Count >= limit) break;
            var target = targetOf(lnk.FullName);
            if (target is null || excluded(target) || !seen.Add(target)) continue;
            bool isFolder = Directory.Exists(target);
            if (!isFolder && !File.Exists(target)) continue;
            list.Add(Make(target, isFolder, lnk.LastWriteTime));
        }
        return list;
    }

    /// <summary>Everything's hits as results, skipping excluded paths.</summary>
    public static IReadOnlyList<SearchResult> FromItems(IEnumerable<EverythingItem> items, Func<string, bool> excluded) =>
        items.Where(i => !excluded(i.FullPath)).Select(i => Make(i.FullPath, i.IsFolder, i.Modified)).ToList();

    /// <summary>The results whose name matches <paramref name="query"/>, in their order; everything when the query is empty.</summary>
    public static IReadOnlyList<SearchResult> Filter(IReadOnlyList<SearchResult> items, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return items;
        // ponytail: plain fuzzy name match, no path matching; add when people search by folder names.
        return items.Where(r => FuzzyMatcher.Score(query, r.Title) >= 50).ToList();
    }

    static SearchResult Make(string target, bool isFolder, DateTime? modified) => new()
    {
        Title = Path.GetFileName(target.TrimEnd('\\')) is { Length: > 0 } name ? name : target,
        Subtitle = FileTypeLabel.For(Path.GetFileName(target.TrimEnd('\\')), isFolder),
        Kind = isFolder ? ResultKind.Folder : ResultKind.File,
        Target = target,
        IconSource = target,
        RevealPath = target,
        Modified = modified,
        NameMatch = true,
        Score = 0,
    };
}
