using Quicklight.Core.Currency;
using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>
/// "100달러", "5만원 엔", "$20 in eur": converts with cached daily rates. The main row goes to the requested currency
/// (or the default one), a few rows below show other common currencies. Enter copies the number.
/// </summary>
public sealed class CurrencyProvider(ExchangeRateStore store, QuicklightSettings settings) : IResultProvider
{
    static readonly string[] Common = ["KRW", "USD", "EUR", "JPY", "CNY"];

    public string Name => "currency";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        if (!settings.CurrencyConversion || query.IsAlternate) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        store.RefreshInBackgroundIfStale();
        if (store.Current is not { } table || CurrencyParser.Parse(query.Text, table.Has) is not { } q)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        // The Windows region's currency ("auto"); amounts already in it go to dollars.
        var configured = SearchEngine.ResolveDefaultCurrency(settings.DefaultCurrency);
        var preferred = table.Has(configured) ? configured : "USD";
        var target = q.To ?? (q.From == preferred ? (preferred == "USD" ? "KRW" : "USD") : preferred);
        if (!double.IsFinite(table.Convert(q.Amount, q.From, target))) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        var results = new List<SearchResult> { Make(table, q, target, Scores.Currency) };

        // With no explicit target, also show the other common currencies.
        if (q.To is null)
        {
            int i = 1;
            foreach (var code in Common.Where(c => c != q.From && c != target && table.Has(c) && double.IsFinite(table.Convert(q.Amount, q.From, c))).Take(3))
                results.Add(Make(table, q, code, Scores.Currency - 100 - i++));
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public static SearchResult Make(RateTable table, CurrencyQuery q, string to, double score)
    {
        double value = table.Convert(q.Amount, q.From, to);
        double rate = table.Rate(q.From, to);
        return new SearchResult
        {
            Title = Currencies.Format(value, to),
            Subtitle = $"{Currencies.Format(q.Amount, q.From)} → {Currencies.KoreanName(to)}   ·   1 {q.From} = {Currencies.FormatRate(rate)} {to}" +
                       $"   ·   {table.UpdatedUtc.ToLocalTime():M월 d일} 기준 ({table.Source})",
            Kind = ResultKind.Currency,
            Target = Currencies.FormatNumber(value, to, grouping: false),
            Action = ActionType.Copy,
            Score = score,
        };
    }
}
