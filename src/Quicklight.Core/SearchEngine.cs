using Quicklight.Core.Currency;
using Quicklight.Core.Everything;
using Quicklight.Core.Matching;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;

namespace Quicklight.Core;

/// <summary>
/// Prefix-free search: the query goes to every provider at once, scores share one scale, usage learning is added,
/// and the merged list is deduped and capped. When the query was typed in the wrong keyboard layout
/// ("zkzkdhxhr" / "ㅍㄴ챙ㄷ") the converted text is searched too.
/// </summary>
public sealed class SearchEngine : IDisposable
{
    readonly QuicklightSettings _settings;
    readonly UsageStore _usage;
    readonly List<IResultProvider> _providers;
    readonly EverythingClient? _everything;
    readonly TimeSpan _fileTimeout;

    public AppProvider Apps { get; }

    /// <summary>Exchange rates for currency conversion; null when conversion is not wired in.</summary>
    public ExchangeRateStore? Rates { get; }

    /// <summary>Where conversions go when no target is given: the setting, or the Windows region's currency for "auto".</summary>
    public string DefaultCurrency => ResolveDefaultCurrency(_settings.DefaultCurrency);

    public static string ResolveDefaultCurrency(string? setting)
    {
        var s = (setting ?? "").Trim().ToUpperInvariant();
        if (s.Length == 3 && s != "AUTO") return s;
        // The Windows region (Settings > Time & language > Region), not the number-format culture.
        try { return System.Globalization.RegionInfo.CurrentRegion.ISOCurrencySymbol; }
        catch (ArgumentException) { return "USD"; }
    }

    public SearchEngine(QuicklightSettings settings, UsageStore usage, AppProvider? apps = null, EverythingClient? everything = null,
        IEnumerable<IResultProvider>? extraProviders = null, TimeSpan? fileTimeout = null, bool probeRemotePaths = true,
        ExchangeRateStore? rates = null, bool includeWindows = false)
    {
        _settings = settings;
        _usage = usage;
        _everything = everything;
        // Long enough for the Full stage's two back-to-back Everything queries (3 s each in the launcher).
        _fileTimeout = fileTimeout ?? TimeSpan.FromSeconds(7);
        Apps = apps ?? new AppProvider();
        _providers =
        [
            new CalculatorProvider(),
            new UrlProvider(),
            new PathProvider(probeRemotePaths),
            Apps,
            new SystemProvider(),
            new WebSearchProvider(settings),
            new UpdateProvider(settings),
            new UnitProvider(),
            new DateProvider(),
            new SnippetProvider(settings),
            new CustomCommandProvider(settings),
            new ProcessProvider(),
        ];
        if (includeWindows) _providers.Add(new WindowProvider());
        if (everything is not null) _providers.Add(new EverythingProvider(everything, settings));
        Rates = rates;
        if (rates is not null) _providers.Add(new CurrencyProvider(rates, settings));
        if (extraProviders is not null) _providers.AddRange(extraProviders);
    }

    /// <summary>Creates an engine wired to the real app index and Everything.</summary>
    public static SearchEngine CreateDefault(QuicklightSettings settings, UsageStore usage, IEnumerable<IResultProvider>? extraProviders = null) =>
        new(settings, usage, new AppProvider(), new EverythingClient(), extraProviders, rates: CreateRateStore(), includeWindows: true);

    public static ExchangeRateStore CreateRateStore() => new(new HttpRateSource(), ExchangeRateStore.DefaultCachePath);

    public Task WarmUpAsync()
    {
        if (_settings.CurrencyConversion) Rates?.RefreshInBackgroundIfStale();
        return Apps.RefreshAsync(TimeSpan.FromMinutes(10));
    }

    /// <param name="files">File search depth. The launcher paints <see cref="FileStage.None"/> first, then Prefix, then Full.</param>
    /// <param name="expandSystemFolders">Show system (hidden/system-attribute) folders instead of the "시스템 폴더" placeholder.</param>
    /// <param name="expandSystemFiles">Show system (hidden/system-attribute) files instead of the "시스템 파일" placeholder.</param>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string text, int? maxResults = null, CancellationToken ct = default,
        ISet<ResultKind>? kinds = null, FileStage files = FileStage.Full, bool expandSystemFolders = false, bool expandSystemFiles = false)
    {
        var query = text.Trim();
        if (query.Length == 0) return [];
        int max = maxResults ?? _settings.ResultLimit;

        var contexts = new List<QueryContext> { new(query, Files: files, ExpandSystemFolders: expandSystemFolders, ExpandSystemFiles: expandSystemFiles) };
        // The layout-converted retry skips Everything: another round-trip per keystroke costs more than it finds.
        var variants = Variants(query);
        foreach (var variant in variants.Skip(1))
            contexts.Add(new QueryContext(variant, IsAlternate: true, Files: FileStage.None));

        var tasks = new List<Task<IReadOnlyList<SearchResult>>>();
        foreach (var ctx in contexts)
            foreach (var p in _providers)
                tasks.Add(RunProvider(p, ctx, ct));
        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var merged = new Dictionary<string, SearchResult>();
        foreach (var r in all.SelectMany(x => x))
        {
            if (kinds is not null && !kinds.Contains(r.Kind)) continue;
            if (!IsTransient(r.Kind))
                r.Score += _usage.Boost(r.Key, query.Length, () => variants.Max(v => FuzzyMatcher.Score(v, r.Title)));
            if (!merged.TryGetValue(r.Key, out var existing) || existing.Score < r.Score)
                merged[r.Key] = r;
        }

        // A file that belongs to an app on the list (its executable, a desktop shortcut, its installer) is that app: one row.
        var listedApps = merged.Values.Select(r => r.App).OfType<AppEntry>().ToHashSet();
        if (listedApps.Count > 0)
            foreach (var r in merged.Values.Where(r => r.Kind == ResultKind.File && Apps.OwnerOf(r.Target) is { } owner && listedApps.Contains(owner)).ToList())
                merged.Remove(r.Key);

        merged.TryGetValue(WebSearchProvider.Make(query, _settings).Key, out var web);
        return Rank(merged.Values, web, max, _settings);
    }

    /// <summary>Folders whose name matches keep this many slots even when apps and files outrank them.</summary>
    const int ReservedFolderSlots = 3;

    /// <summary>
    /// Picks the final list: best first, files and folders capped separately, a few slots kept for folders whose
    /// name matches (otherwise apps and recent files would crowd out "Downloads" or "Project"), web search last.
    /// </summary>
    internal static List<SearchResult> Rank(IEnumerable<SearchResult> candidates, SearchResult? web, int max, QuicklightSettings settings)
    {
        // Web search is always offered and always last, in the last slot (unless there is only one slot).
        bool addWeb = web is not null && max > 1;
        int slots = max - (addWeb ? 1 : 0);

        // The "시스템 폴더"/"시스템 파일" placeholders always sort last (before web search), never competing for a
        // slot with real results: skip them here and append after everything else is picked.
        var summaries = candidates.Where(r => r.IsSystemSummary).ToList();
        int summarySlots = Math.Min(summaries.Count, Math.Max(0, slots - 1)); // the top hit stays free

        var ordered = candidates.Where(r => r != web && !r.IsSystemSummary)
            .OrderByDescending(r => r.Score).ThenBy(r => r.Title.Length).ToList();

        int maxFolders = Math.Max(0, settings.FolderResultLimit);
        int othersSlots = slots - summarySlots;
        var reserved = ordered.Where(r => r.Kind == ResultKind.Folder && r.NameMatch)
            .Take(Math.Min(Math.Min(ReservedFolderSlots, maxFolders), Math.Max(0, othersSlots - 1))) // the top hit stays free
            .ToHashSet();

        var picked = new HashSet<SearchResult>(reserved);
        int others = 0, files = 0, folders = reserved.Count;
        foreach (var r in ordered)
        {
            if (others >= othersSlots - reserved.Count) break;
            if (reserved.Contains(r)) continue;
            if (r.Kind == ResultKind.File && ++files > settings.FileResultLimit) continue;
            if (r.Kind == ResultKind.Folder && ++folders > maxFolders) continue;
            picked.Add(r);
            others++;
        }

        var ranked = ordered.Where(picked.Contains).ToList(); // keep score order
        ranked.AddRange(summaries.Take(summarySlots));
        if (addWeb) ranked.Add(web!);
        else if (ranked.Count == 0 && web is not null) ranked.Add(web);
        return ranked;
    }

    async Task<IReadOnlyList<SearchResult>> RunProvider(IResultProvider p, QueryContext ctx, CancellationToken ct)
    {
        try
        {
            var task = p.QueryAsync(ctx, ct);
            if (p is EverythingProvider)
            {
                // A slow Everything must not hold back the instant results.
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var done = await Task.WhenAny(task, Task.Delay(_fileTimeout, timer.Token)).ConfigureAwait(false);
                timer.Cancel(); // release the delay's timer as soon as Everything answers
                if (done != task) return [];
            }
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"provider {p.Name} failed", ex); // not the query: the log must not become a search history
            return [];
        }
    }

    /// <summary>How well the query (or its layout conversion) matches the result's name: what the history keeps instead of the query.</summary>
    static double Strength(string query, SearchResult r) => Variants(query.Trim()).Max(v => FuzzyMatcher.Score(v, r.Title));

    /// <summary>The query as typed, then its keyboard-layout conversion ("zkzkdhxhr" → "카카오") and its jamo-order fix ("ㅏㅈ" → "자").</summary>
    static List<string> Variants(string query)
    {
        var list = new List<string> { query };
        if ((Hangul.QwertyToHangul(query) ?? Hangul.HangulToQwerty(query)) is { } alt && !list.Contains(alt)) list.Add(alt);
        if (Hangul.FixJamoOrder(query) is { } fixedOrder && !list.Contains(fixedOrder)) list.Add(fixedOrder);
        return list;
    }

    /// <summary>How well the query (or its layout conversion) matches the result's name: what the history keeps instead of the query.</summary>
    /// <summary>Remembers the choice so queries like this one rank it higher next time. The query itself is not kept.</summary>
    public void RecordSelection(string query, SearchResult result)
    {
        if (IsTransient(result.Kind)) return;
        _usage.Record(result.Key, query.Trim().Length, Strength(query, result));
    }

    /// <summary>Answers and rows whose identity does not last (a window handle, a clipboard entry): no usage learning.</summary>
    static bool IsTransient(ResultKind k) =>
        k is ResultKind.Calculator or ResultKind.Currency or ResultKind.WebSearch or ResultKind.Window or ResultKind.Clipboard or ResultKind.Process;

    /// <summary>Runs Open/System actions. Copy actions are the caller's job (clipboard needs a UI thread).</summary>
    public static void Execute(SearchResult r)
    {
        switch (r.Action)
        {
            case ActionType.Open when r.Arguments is not null:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Target, r.Arguments)
                    { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(r.Target) ?? "" })?.Dispose();
                break;
            case ActionType.Open:
                // Files and programs start in their own folder; a shortcut brings its own.
                bool inOwnFolder = r.Kind is ResultKind.File || r.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                ShellLauncher.Open(r.Target, inOwnFolder ? Path.GetDirectoryName(r.Target) : null);
                break;
            case ActionType.System:
                ShellLauncher.RunSystemCommand(r.Target);
                break;
            case ActionType.SwitchWindow:
                WindowProvider.Activate(long.Parse(r.Target));
                break;
            case ActionType.Kill:
                ProcessProvider.Kill(r.Target);
                break;
            case ActionType.Copy or ActionType.Paste:
                throw new InvalidOperationException("Copy and paste results are handled by the caller.");
            case ActionType.None:
                break;
            case ActionType.Update:
                throw new InvalidOperationException("Updates are handled by the launcher.");
        }
    }

    public void Dispose()
    {
        _everything?.Dispose();
    }
}
