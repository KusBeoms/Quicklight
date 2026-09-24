using Quicklight.Core.Models;
using Quicklight.Core.Translation;

namespace Quicklight.Core.Providers;

/// <summary>
/// "hello 번역", "사과 영어로", "good morning in korean": translates with a local LibreTranslate server.
/// The instant pass only shows a cached translation (or a "translating…" row); the later passes call the server,
/// the same way file results arrive after the instant results. Enter copies the translation.
/// </summary>
public sealed class TranslationProvider(ITranslator translator, QuicklightSettings settings) : IResultProvider
{
    const int CacheSize = 100;
    readonly Dictionary<(string, string), TranslationResult> _cache = [];
    readonly Queue<(string, string)> _order = new();
    readonly object _gate = new();

    public string Name => "translation";

    public async Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (!settings.Translation || query.IsAlternate || TranslationParser.Parse(query.Text) is not { } req) return [];
        var system = Languages.System;
        var secondary = string.IsNullOrWhiteSpace(settings.SecondaryLanguage) ? "en" : settings.SecondaryLanguage.Trim().ToLowerInvariant();
        IReadOnlyCollection<string> models = settings.TranslationLanguages is { Count: > 0 } l ? l : LibreTranslateClient.DefaultModels;
        var target = req.Target ?? TranslationParser.DefaultTarget(req.Text, system, secondary, models);

        TranslationResult? cached;
        lock (_gate) _cache.TryGetValue((req.Text, target), out cached);
        if (cached is not null) return Rows(cached);
        if (query.Files == FileStage.None) return [Pending(req, target, "번역 중…")];

        // Never hold the other results back for long: a cold server shows a "preparing" row, and the launcher
        // searches again once the server is up.
        var wait = query.Files == FileStage.Full ? TimeSpan.FromSeconds(3) : TimeSpan.Zero;
        if (!translator.IsReady && !await translator.EnsureReadyAsync(wait, ct).ConfigureAwait(false))
            return [Pending(req, target, translator.Status ?? "번역 엔진을 준비하는 중…")];

        try
        {
            var result = await translator.TranslateAsync(req.Text, target, ct).ConfigureAwait(false);
            // Detected as the target already ("hello 번역" on an English system): go to the fallback language instead.
            if (req.Target is null && result.Source == target)
                result = await translator.TranslateAsync(req.Text, TranslationParser.Fallback(target, secondary, models), ct).ConfigureAwait(false);
            Remember((req.Text, target), result);
            return Rows(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [Pending(req, target, "번역이 너무 오래 걸립니다. 잠시 뒤 다시 시도하세요")]; // HttpClient timeout
        }
        catch (Exception ex) when (ex is TranslationException or HttpRequestException)
        {
            Log.Error("translation failed", ex);
            return [Pending(req, target, "번역하지 못했습니다: " + ex.Message)];
        }
    }

    void Remember((string, string) key, TranslationResult r)
    {
        lock (_gate)
        {
            if (_cache.TryAdd(key, r)) _order.Enqueue(key);
            while (_order.Count > CacheSize) _cache.Remove(_order.Dequeue());
        }
    }

    static IReadOnlyList<SearchResult> Rows(TranslationResult r)
    {
        var rows = new List<SearchResult> { Make(r.Text, r, Scores.Translation, main: true) };
        int i = 1;
        foreach (var alt in r.Alternatives) rows.Add(Make(alt, r, Scores.Translation - 100 - i++, main: false));
        return rows;
    }

    static SearchResult Make(string text, TranslationResult r, double score, bool main) => new()
    {
        Title = text,
        Subtitle = main
            ? $"{Languages.KoreanName(r.Source)} → {Languages.KoreanName(r.Target)}   ·   LibreTranslate" +
              (r.Confidence is { } c && r.Source != "auto" ? $" (언어 감지 {c:0}%)" : "")
            : "다른 번역",
        Kind = ResultKind.Translation,
        Target = text,
        Action = ActionType.Copy,
        Score = score,
    };

    static SearchResult Pending(TranslationRequest req, string target, string message) => new()
    {
        Title = message,
        Subtitle = $"“{req.Text}” → {Languages.KoreanName(target)}",
        Kind = ResultKind.Translation,
        Target = req.Text,
        Action = ActionType.None,
        // Below apps and settings: an informational row must not take Enter away from a real result.
        Score = Scores.TranslationPending,
    };
}
