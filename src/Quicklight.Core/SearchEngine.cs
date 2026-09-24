using Quicklight.Core.Currency;
using Quicklight.Core.Everything;
using Quicklight.Core.Matching;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Quicklight.Core.Translation;

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

    /// <summary>Target for text already in the system language (English by default).</summary>
    public string SecondaryLanguage => string.IsNullOrWhiteSpace(_settings.SecondaryLanguage) ? "en" : _settings.SecondaryLanguage.Trim().ToLowerInvariant();

    /// <summary>Local translation server; null when translation is not wired in.</summary>
    public ITranslator? Translator { get; }

    /// <summary>Where conversions go when no target is given: the setting, or the Windows region's currency for "auto".</summary>
    public string DefaultCurrency => ResolveDefaultCurrency(_settings.DefaultCurrency);

    public static string ResolveDefaultCurrency(string? setting)
    {
        var s = (setting ?? "").Trim().ToUpperInvariant();
        if (s.Length == 3 && s != "AUTO") return s;
        try { return new System.Globalization.RegionInfo(System.Globalization.CultureInfo.CurrentCulture.Name).ISOCurrencySymbol; }
        catch (ArgumentException) { return "USD"; } // invariant or neutral culture
    }

    public SearchEngine(QuicklightSettings settings, UsageStore usage, AppProvider? apps = null, EverythingClient? everything = null,
        IEnumerable<IResultProvider>? extraProviders = null, TimeSpan? fileTimeout = null, bool probeRemotePaths = true,
        ExchangeRateStore? rates = null, ITranslator? translator = null)
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
        ];
        if (everything is not null) _providers.Add(new EverythingProvider(everything, settings));
        Rates = rates;
        if (rates is not null) _providers.Add(new CurrencyProvider(rates, settings));
        Translator = translator;
        if (translator is not null) _providers.Add(new TranslationProvider(translator, settings));
        if (extraProviders is not null) _providers.AddRange(extraProviders);
    }

    /// <summary>Creates an engine wired to the real app index and Everything.</summary>
    public static SearchEngine CreateDefault(QuicklightSettings settings, UsageStore usage) =>
        new(settings, usage, new AppProvider(), new EverythingClient(), rates: CreateRateStore(), translator: CreateTranslator(settings));

    /// <summary>LibreTranslate client for the settings, or null when translation is off or the URL is not local.</summary>
    public static LibreTranslateClient? CreateTranslator(QuicklightSettings settings)
    {
        if (!settings.Translation) return null;
        try
        {
            return new LibreTranslateClient(settings.LibreTranslateUrl, LibreTranslateClient.FindServerDir(settings.LibreTranslateDir),
                settings.TranslationLanguages is { Count: > 0 } l ? l : null);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            Log.Error("translation disabled", ex);
            return null;
        }
    }

    public static ExchangeRateStore CreateRateStore() => new(new HttpRateSource(), ExchangeRateStore.DefaultCachePath);

    public Task WarmUpAsync()
    {
        if (_settings.CurrencyConversion) Rates?.RefreshInBackgroundIfStale();
        return Apps.RefreshAsync(TimeSpan.FromMinutes(10));
    }

    /// <param name="files">File search depth. The launcher paints <see cref="FileStage.None"/> first, then Prefix, then Full.</param>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string text, int? maxResults = null, CancellationToken ct = default,
        ISet<ResultKind>? kinds = null, FileStage files = FileStage.Full)
    {
        var query = text.Trim();
        if (query.Length == 0) return [];
        int max = maxResults ?? _settings.MaxResults;

        var contexts = new List<QueryContext> { new(query, Files: files) };
        var alt = Hangul.QwertyToHangul(query) ?? Hangul.HangulToQwerty(query);
        // The layout-converted retry skips Everything: another round-trip per keystroke costs more than it finds.
        if (alt is not null && alt != query) contexts.Add(new QueryContext(alt, IsAlternate: true, Files: FileStage.None));

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
            if (r.Kind is not (ResultKind.Calculator or ResultKind.Currency or ResultKind.Translation or ResultKind.WebSearch))
                r.Score += _usage.Boost(query, r.Key);
            if (!merged.TryGetValue(r.Key, out var existing) || existing.Score < r.Score)
                merged[r.Key] = r;
        }

        // Web search is always offered and always last, in the last slot (unless there is only one slot).
        merged.TryGetValue(WebSearchProvider.Make(query, _settings).Key, out var web);
        bool addWeb = web is not null && max > 1;
        int slots = max - (addWeb ? 1 : 0);
        var ranked = new List<SearchResult>();
        int fileCount = 0;
        foreach (var r in merged.Values.OrderByDescending(r => r.Score).ThenBy(r => r.Title.Length))
        {
            if (ranked.Count >= slots) break;
            if (r.Kind == ResultKind.WebSearch) continue;
            if (r.Kind is ResultKind.File or ResultKind.Folder && ++fileCount > _settings.MaxFileResults) continue;
            ranked.Add(r);
        }
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
            Log.Error($"provider {p.Name} failed for '{ctx.Text}'", ex);
            return [];
        }
    }

    /// <summary>Remembers the choice so the same query ranks it higher next time.</summary>
    public void RecordSelection(string query, SearchResult result)
    {
        if (result.Kind is ResultKind.Calculator or ResultKind.Currency or ResultKind.Translation or ResultKind.WebSearch) return;
        _usage.Record(query, result.Key);
    }

    /// <summary>Runs Open/System actions. Copy actions are the caller's job (clipboard needs a UI thread).</summary>
    public static void Execute(SearchResult r)
    {
        switch (r.Action)
        {
            case ActionType.Open when r.Arguments is not null:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Target, r.Arguments) { UseShellExecute = true })?.Dispose();
                break;
            case ActionType.Open:
                ShellLauncher.Open(r.Target, r.Kind is ResultKind.File ? Path.GetDirectoryName(r.Target) : null);
                break;
            case ActionType.System:
                ShellLauncher.RunSystemCommand(r.Target);
                break;
            case ActionType.Copy:
                throw new InvalidOperationException("Copy results are handled by the caller.");
            case ActionType.None:
                break;
        }
    }

    public void Dispose()
    {
        _everything?.Dispose();
        (Translator as IDisposable)?.Dispose(); // stops a LibreTranslate server we started
    }
}
