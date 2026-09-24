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
        var target = req.Target ?? TranslationParser.DefaultTarget(req.Text, system, secondary);

        TranslationResult? cached;
        lock (_gate) _cache.TryGetValue((req.Text, target), out cached);
        if (cached is not null) return Rows(cached);
        if (query.Files == FileStage.None) return [Pending(req, target, "번역 중…")];

        // The prefix pass does not wait for a cold server; the full pass does (the launcher shows "starting" meanwhile).
        var wait = query.Files == FileStage.Full ? TimeSpan.FromSeconds(45) : TimeSpan.Zero;
        if (!translator.IsReady && !await translator.EnsureReadyAsync(wait, ct).ConfigureAwait(false))
            return [Pending(req, target, "번역 엔진을 준비하는 중… 처음에는 언어 모델을 내려받느라 몇 분 걸릴 수 있습니다")];

        try
        {
            var result = await translator.TranslateAsync(req.Text, target, ct).ConfigureAwait(false);
            Remember((req.Text, target), result);
            return Rows(result);
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
        Score = Scores.Translation,
    };
}
