using Quicklight.Core.Everything;
using Quicklight.Core.Matching;
using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>
/// File and folder search over the Everything index. Pulls two candidate sets (recently modified matches and
/// name-prefix matches), then re-ranks them by name match quality, recency, depth and noisy locations.
/// </summary>
public sealed class EverythingProvider(EverythingClient client, QuicklightSettings settings) : IResultProvider
{
    public string Name => "files";

    /// <summary>Characters that mean the user is writing Everything syntax, so we pass the query through untouched.</summary>
    static readonly char[] SyntaxChars = [':', '*', '?', '|', '<', '>', '"', '\\', '!'];

    // The prefix pass result for the last query, reused by the full pass that follows it.
    (string Query, IReadOnlyList<EverythingItem> Items, DateTime At)? _prefixCache;
    static readonly TimeSpan PrefixCacheLifetime = TimeSpan.FromSeconds(3);
    readonly object _cacheGate = new();

    public async Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var q = query.Text;
        if (!settings.FileSearch || query.Files == FileStage.None || q.Length < 2 || !EverythingClient.IsAvailable) return [];

        // A substring query makes Everything scan every name (about a second on a very large index);
        // startwith: is several times faster. So the prefix pass comes first and the full pass reuses it.
        bool raw = q.IndexOfAny(SyntaxChars) >= 0;
        var sets = new List<IReadOnlyList<EverythingItem>>();
        try
        {
            if (!raw)
            {
                IReadOnlyList<EverythingItem>? prefix = null;
                lock (_cacheGate)
                    if (_prefixCache is { } c && c.Query == q && DateTime.UtcNow - c.At < PrefixCacheLifetime) prefix = c.Items;
                if (prefix is null)
                {
                    var start = $"startwith:\"{q.Replace("\"", "")}\"";
                    var any = await client.SearchAsync(new EverythingQuery(start, 40, EverythingSort.NameAscending), ct).ConfigureAwait(false);
                    // Folders get their own candidates: among thousands of same-named files (desktop.ini, src\...)
                    // they would rarely make the first 40. Folder-only queries are fast (far fewer folders than files).
                    var folderItems = await client.SearchAsync(new EverythingQuery("folder: " + start, 20, EverythingSort.NameAscending), ct).ConfigureAwait(false);
                    prefix = [.. any, .. folderItems];
                    lock (_cacheGate) _prefixCache = (q, prefix, DateTime.UtcNow);
                }
                sets.Add(prefix);
            }
            if (raw || query.Files == FileStage.Full)
            {
                sets.Add(await client.SearchAsync(new EverythingQuery(q, 60, EverythingSort.DateModifiedDescending), ct).ConfigureAwait(false));
                if (!raw) sets.Add(await client.SearchAsync(new EverythingQuery("folder: " + q, 30, EverythingSort.DateModifiedDescending), ct).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is EverythingUnavailableException or TimeoutException)
        {
            if (sets.Count == 0) return [];
        }

        var now = DateTime.Now;
        var byPath = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sets.SelectMany(s => s))
        {
            var full = item.FullPath;
            if (byPath.ContainsKey(full) || IsExcluded(full)) continue;
            // Start menu shortcuts duplicate the app results.
            if (full.Contains(@"\Start Menu\Programs\", StringComparison.OrdinalIgnoreCase) && full.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;

            double m = raw ? 60 : FuzzyMatcher.Score(q, item.Name);
            bool nameMatch = raw || m >= query.MinMatch;
            if (!nameMatch)
            {
                // Everything matched something the fuzzy matcher did not (e.g. a multi-word hit across the path).
                if (query.IsAlternate) continue;
                m = 45;
            }
            byPath[full] = new SearchResult
            {
                Title = item.Name,
                Subtitle = item.IsFolder ? full : item.Directory,
                Kind = item.IsFolder ? ResultKind.Folder : ResultKind.File,
                Target = full,
                IconSource = full,
                RevealPath = full,
                Modified = item.Modified,
                Size = item.Size >= 0 ? item.Size : null,
                NameMatch = nameMatch,
                IsSystem = item.IsSystem,
                Score = Score(m, item, full, now),
            };
        }

        // System (hidden/system-attribute) files and folders are noise to most people ($RECYCLE.BIN, System Volume
        // Information, ...): left out by default, one at a time via "system-folders"/"system-files" placeholder
        // rows (Enter re-runs the search with QueryContext.ExpandSystemFolders/Files set).
        var all = byPath.Values;
        var files = Partition(all, ResultKind.File, query.ExpandSystemFiles, "시스템 파일", "system-files", settings.MaxFileResults);
        var folders = Partition(all, ResultKind.Folder, query.ExpandSystemFolders, "시스템 폴더", "system-folders", settings.MaxFolderResults);
        return [.. files, .. folders];
    }

    /// <summary>Normal (non-system) items of <paramref name="kind"/>, capped and ranked, plus a placeholder for the system ones when not expanded.</summary>
    static List<SearchResult> Partition(IEnumerable<SearchResult> all, ResultKind kind, bool expand,
        string summaryTitle, string summaryTarget, int cap)
    {
        var ofKind = all.Where(r => r.Kind == kind).ToList();
        var normal = ofKind.Where(r => !r.IsSystem).OrderByDescending(r => r.Score).Take(Math.Max(cap * 3, 12)).ToList();
        if (expand)
        {
            normal.AddRange(ofKind.Where(r => r.IsSystem).OrderByDescending(r => r.Score));
            return normal;
        }
        int systemCount = ofKind.Count(r => r.IsSystem);
        if (systemCount > 0)
            normal.Add(new SearchResult
            {
                Title = summaryTitle,
                Subtitle = $"{systemCount}개 숨김 · Enter로 펼치기",
                Kind = kind,
                Target = summaryTarget,
                Action = ActionType.Expand,
                IsSystemSummary = true,
                Score = 0,
            });
        return normal;
    }

    double Score(double match, EverythingItem item, string full, DateTime now)
    {
        double s = Scores.FileBase + 0.9 * match;
        if (item.Modified is { } mod)
        {
            var age = now - mod;
            s += age.TotalDays < 1 ? 12 : age.TotalDays < 7 ? 8 : age.TotalDays < 30 ? 4 : 0;
        }
        if (item.IsFolder) s += 4;
        int depth = full.Count(c => c == '\\');
        s -= Math.Min(10, depth * 0.8);
        if (settings.DemotedPaths.Any(d => full.Contains(d, StringComparison.OrdinalIgnoreCase))) s -= 30;
        return s;
    }

    bool IsExcluded(string full) => settings.ExcludedPaths.Any(d => full.Contains(d, StringComparison.OrdinalIgnoreCase));
}
