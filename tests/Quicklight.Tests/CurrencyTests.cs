using System.Text.Json.Nodes;
using Quicklight.Core;
using Quicklight.Core.Currency;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Quicklight.Mcp;

namespace Quicklight.Tests;

public class CurrencyTests
{
    // Case-insensitive like the production tables.
    internal static readonly RateTable Table = new("USD", new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1, ["KRW"] = 1400, ["JPY"] = 150, ["EUR"] = 0.9, ["CNY"] = 7, ["GBP"] = 0.75, ["HKD"] = 7.8,
        ["VND"] = 25000, ["CHF"] = 0.8, ["THB"] = 35, ["AUD"] = 1.5, ["IDR"] = 16000,
        ["PHP"] = 56, ["TOP"] = 2.3, ["ALL"] = 90, ["CUP"] = 24, ["MAD"] = 10, ["TRY"] = 34,
    }, new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc), "test");

    static CurrencyQuery? P(string s) => CurrencyParser.Parse(s, Table.Has);

    [Theory]
    [InlineData("100달러", 100, "USD", null)]
    [InlineData("$100", 100, "USD", null)]
    [InlineData("$ 1,250.50", 1250.5, "USD", null)]
    [InlineData("100 usd", 100, "USD", null)]
    [InlineData("100USD to KRW", 100, "USD", "KRW")]
    [InlineData("100 usd in eur", 100, "USD", "EUR")]
    [InlineData("100 usd krw", 100, "USD", "KRW")]
    [InlineData("5만원", 50000, "KRW", null)]
    [InlineData("5만원 엔", 50000, "KRW", "JPY")]
    [InlineData("1.2억 원 usd", 120000000, "KRW", "USD")]
    [InlineData("3천만원", 30000000, "KRW", null)]
    [InlineData("100달러를 유로로", 100, "USD", "EUR")]
    [InlineData("100달러는 몇 원?", 100, "USD", "KRW")]
    [InlineData("100 달러 원화로 환전", 100, "USD", "KRW")]
    [InlineData("€20 → $", 20, "EUR", "USD")]
    [InlineData("1000엔", 1000, "JPY", null)]
    [InlineData("50 홍콩달러", 50, "HKD", null)]
    [InlineData("10 dollars", 10, "USD", null)]
    [InlineData("2 chf", 2, "CHF", null)]
    [InlineData("100 php", 100, "PHP", null)]
    [InlineData("100000 베트남동", 100000, "VND", null)]
    [InlineData("100   usd     to    krw", 100, "USD", "KRW")]
    [InlineData("3 CUP", 3, "CUP", null)]             // upper case: clearly meant as a code
    [InlineData("5백만원", 5000000, "KRW", null)]
    [InlineData("1억 2천만원", 120000000, "KRW", null)]
    [InlineData("1억2천만원 달러", 120000000, "KRW", "USD")]
    [InlineData("5만천원", 51000, "KRW", null)]
    [InlineData("1억 2천3백만원", 123000000, "KRW", null)]
    [InlineData("3십만원", 300000, "KRW", null)]
    [InlineData("100달러 환전 얼마", 100, "USD", null)]
    public void Parses(string text, double amount, string from, string? to)
    {
        var q = P(text);
        Assert.NotNull(q);
        Assert.Equal(amount, q!.Amount, 6);
        Assert.Equal(from, q.From);
        Assert.Equal(to, q.To);
    }

    [Theory]
    [InlineData("달러")]              // no amount
    [InlineData("100")]               // no currency
    [InlineData("100 abc")]           // unknown code
    [InlineData("100달러 환율 추이")]   // extra words: an ordinary search
    [InlineData("usd krw")]
    [InlineData("0원")]
    [InlineData("100 usd to xyz")]
    [InlineData("100 files")]
    [InlineData("12*3")]
    [InlineData("php 8")]             // a bare ISO code before the number is a search, not money
    [InlineData("top 10")]
    [InlineData("all 3")]
    [InlineData("101동")]              // apartment building, not Vietnamese dong
    [InlineData("3 dong")]
    [InlineData("3 cup")]             // common words that happen to be ISO codes
    [InlineData("2 mad")]
    [InlineData("5 all")]
    [InlineData("5만만원")]            // repeated group unit is not a number
    [InlineData("5천천원")]
    [InlineData("5 pen")]
    [InlineData("100달러 all")]
    public void Ignores(string text) => Assert.Null(P(text));

    [Fact]
    public void Whitespace_runs_stay_fast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CurrencyParser.Parse("100 usd" + new string(' ', 72) + "x", Table.Has);
        Assert.True(sw.ElapsedMilliseconds < 50, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Same_source_and_target_means_no_explicit_target() => Assert.Null(P("100 usd to usd")!.To);

    [Fact]
    public void Converts_through_the_base()
    {
        Assert.Equal(140000, Table.Convert(100, "USD", "KRW"), 6);
        Assert.Equal(15000.0 / 1400, Table.Convert(15000, "KRW", "USD"), 9);
        Assert.Equal(150 / 1400.0 * 10000, Table.Convert(10000, "KRW", "JPY"), 6);
        Assert.Equal(150 / 0.9, Table.Rate("EUR", "JPY"), 9);
    }

    [Theory]
    [InlineData(1374.5, "USD", "$1,374.50")]
    [InlineData(137450.4, "KRW", "₩137,450")]
    [InlineData(1234.567, "CHF", "1,234.57 CHF")]
    [InlineData(0.000714, "USD", "$0.000714")]
    [InlineData(-5, "EUR", "-€5.00")]
    [InlineData(-0.001, "KRW", "-₩0.001")]
    [InlineData(-0.0000000000000001, "USD", "$0.00")]   // rounds to zero: no minus sign
    [InlineData(0.0000000123, "USD", "$0.0000000123")]
    public void Formats(double v, string code, string expected) => Assert.Equal(expected, Currencies.Format(v, code));

    [Theory]
    [InlineData(1364.768247, "1,364.77")]
    [InlineData(6.7074, "6.7074")]
    [InlineData(0.115862, "0.1159")]
    [InlineData(0.000732, "0.000732")]
    [InlineData(0, "—")]
    [InlineData(double.NaN, "—")]
    [InlineData(-3, "—")]
    public void Formats_rates(double rate, string expected) => Assert.Equal(expected, Currencies.FormatRate(rate));

    static SearchEngine Engine(ExchangeRateStore store, QuicklightSettings? settings = null) =>
        new(settings ?? new QuicklightSettings(), new UsageStore(null), new AppProvider([]), rates: store);

    static ExchangeRateStore Store(FakeSource? source = null) => new(source ?? new FakeSource(Table), null);

    [Fact]
    public async Task Engine_puts_the_conversion_on_top_and_copies_the_number()
    {
        var store = Store();
        await store.GetAsync(TimeSpan.FromSeconds(5));
        var r = await Engine(store).SearchAsync("100달러");
        Assert.Equal(ResultKind.Currency, r[0].Kind);
        Assert.Equal("₩140,000", r[0].Title);
        Assert.Equal("140000", r[0].Target);
        Assert.Equal(ActionType.Copy, r[0].Action);
        // Other common currencies follow, the source currency is not repeated.
        var extra = r.Where(x => x.Kind == ResultKind.Currency).Skip(1).Select(x => x.Title).ToList();
        Assert.Equal(3, extra.Count);
        Assert.DoesNotContain(extra, t => t.StartsWith('$'));
    }

    [Fact]
    public async Task Won_goes_to_dollars_and_explicit_targets_win()
    {
        var store = Store();
        await store.GetAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("$10.00", (await Engine(store).SearchAsync("14000원"))[0].Title);
        var jpy = await Engine(store).SearchAsync("100 usd to jpy");
        Assert.Equal("¥15,000", jpy[0].Title);
        Assert.Single(jpy, x => x.Kind == ResultKind.Currency); // explicit target: no extra rows
    }

    [Fact]
    public async Task Default_currency_setting_and_off_switch()
    {
        var store = Store();
        await store.GetAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("€90.00", (await Engine(store, new QuicklightSettings { DefaultCurrency = "EUR" }).SearchAsync("100달러"))[0].Title);
        var off = await Engine(store, new QuicklightSettings { CurrencyConversion = false }).SearchAsync("100달러");
        Assert.DoesNotContain(off, x => x.Kind == ResultKind.Currency);
    }

    [Fact]
    public async Task No_rates_yet_means_no_result_and_a_background_fetch()
    {
        var source = new FakeSource(Table) { Delay = TimeSpan.FromMilliseconds(300) };
        var store = Store(source);
        var r = await Engine(store).SearchAsync("100달러");
        Assert.DoesNotContain(r, x => x.Kind == ResultKind.Currency); // never waits for the network
        await store.GetAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.Calls); // exactly one background fetch was started
        Assert.Contains(await Engine(store).SearchAsync("100달러"), x => x.Kind == ResultKind.Currency);
    }

    [Fact]
    public async Task Store_caches_to_disk_and_backs_off_after_failures()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql-rates-{Guid.NewGuid():N}.json");
        try
        {
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            var ok = new FakeSource(Table);
            var store = new ExchangeRateStore(ok, path, utcNow: () => now);
            await store.GetAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(path));

            // A new process reads the cache: fresh, so no fetch.
            var failing = new FakeSource(null);
            var reloaded = new ExchangeRateStore(failing, path, utcNow: () => now.AddHours(1));
            Assert.Equal(1400, reloaded.Current!.Rates["KRW"]);
            reloaded.RefreshInBackgroundIfStale();
            Assert.Equal(0, failing.Calls);

            // Stale: one attempt, it fails, the cached table stays, and no retry until the back-off passes.
            var clock = now.AddHours(7);
            var stale = new ExchangeRateStore(failing, path, utcNow: () => clock);
            await stale.GetAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, failing.Calls);
            Assert.NotNull(stale.Current);
            stale.RefreshInBackgroundIfStale();
            Assert.Equal(1, failing.Calls);
            clock = clock.AddMinutes(11);
            await stale.GetAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, failing.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cache_from_the_future_counts_as_stale()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql-rates-{Guid.NewGuid():N}.json");
        try
        {
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            await new ExchangeRateStore(new FakeSource(Table), path, utcNow: () => now.AddYears(1)).GetAsync(TimeSpan.FromSeconds(5));
            var store = new ExchangeRateStore(new FakeSource(Table), path, utcNow: () => now); // clock went back a year
            Assert.True(store.IsStale);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tampered_cache_is_revalidated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql-rates-{Guid.NewGuid():N}.json");
        try
        {
            var rates = string.Join(",", Enumerable.Range(0, 12).Select(i => $"\"C{i:00}\":{i + 1}"));
            File.WriteAllText(path, $$$"""{"Base":"USD","Rates":{"USD":1,"usd":1,"KRW":0,"JPY":-5,{{{rates}}}},"UpdatedUtc":"2026-09-24T00:00:00Z","Source":"x","FetchedUtc":"2026-09-24T00:00:00Z"}""");
            var store = new ExchangeRateStore(new FakeSource(null), path);
            Assert.NotNull(store.Current);
            Assert.False(store.Current!.Has("KRW")); // zero rate dropped: no division by zero
            Assert.False(store.Current.Has("JPY"));
            Assert.True(store.Current.Has("usd")); // duplicate-case keys do not crash the load
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_absurd_api_values()
    {
        var many = string.Join(",", Enumerable.Range(0, 12).Select(i => $"\"C{i:00}\":{i + 1}"));
        var t = HttpRateSource.ParseOpenErApi($$$"""{"result":"success","time_last_update_unix":1790208152,"base_code":"USD","rates":{"KRW":1e300,"JPY":0,{{{many}}}}}""");
        Assert.False(t.Has("KRW"));
        Assert.False(t.Has("JPY"));
        Assert.True(t.Has("USD")); // base filled in
        Assert.Throws<FormatException>(() => HttpRateSource.ParseOpenErApi("""{"result":"success","rates":{}}"""));
        Assert.Throws<FormatException>(() => HttpRateSource.ParseFrankfurter("""[1,2,3]"""));
    }

    [Fact]
    public async Task Huge_amounts_do_not_overflow()
    {
        var store = Store();
        await store.GetAsync(TimeSpan.FromSeconds(5));
        var server = new McpServer(Engine(store), null, new McpServer.Options(AllowOpen: false));
        var r = await server.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "convert_currency", ["arguments"] = new JsonObject { ["amount"] = 1e307, ["from"] = "usd", ["to"] = "krw" } },
        });
        Assert.True(r!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Invalid_default_currency_falls_back()
    {
        var store = Store();
        await store.GetAsync(TimeSpan.FromSeconds(5));
        var settings = new QuicklightSettings { DefaultCurrency = "XXX" };
        var server = new McpServer(Engine(store, settings), null, new McpServer.Options(AllowOpen: false));
        var r = await server.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "convert_currency", ["arguments"] = new JsonObject { ["amount"] = 1, ["from"] = "krw" } },
        });
        Assert.Equal("USD", r!["result"]!["structuredContent"]!["to"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_both_api_formats()
    {
        var er = HttpRateSource.ParseOpenErApi("""
            {"result":"success","time_last_update_unix":1790208152,"base_code":"USD",
             "rates":{"USD":1,"KRW":1364.77,"JPY":158.1,"EUR":0.876,"GBP":0.75,"CNY":6.7,"HKD":7.8,"AUD":1.4,"CAD":1.4,"CHF":0.8,"BAD":-1}}
            """);
        Assert.Equal(1364.77, er.Rates["KRW"]);
        Assert.False(er.Has("BAD"));
        Assert.Equal(2026, er.UpdatedUtc.Year);

        var fr = HttpRateSource.ParseFrankfurter("""
            {"amount":1.0,"base":"USD","date":"2026-09-23",
             "rates":{"KRW":1365.35,"JPY":157.92,"EUR":0.876,"GBP":0.75,"CNY":6.7,"HKD":7.8,"AUD":1.4,"CAD":1.4,"CHF":0.8,"SEK":9.5}}
            """);
        Assert.Equal(1, fr.Rates["USD"]); // base added
        Assert.Throws<FormatException>(() => HttpRateSource.ParseOpenErApi("""{"result":"error","error-type":"x"}"""));
    }

    [Fact]
    public async Task Mcp_convert_currency()
    {
        var store = Store();
        var server = new McpServer(Engine(store), null, new McpServer.Options(AllowOpen: false));
        JsonObject Call(JsonObject args) => new()
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "convert_currency", ["arguments"] = args },
        };
        var r = await server.HandleAsync(Call(new JsonObject { ["amount"] = 100, ["from"] = "달러", ["to"] = "엔" }));
        var s = r!["result"]!["structuredContent"]!;
        Assert.Equal("JPY", s["to"]!.GetValue<string>());
        Assert.Equal(15000, s["result"]!.GetValue<double>());
        var dflt = await server.HandleAsync(Call(new JsonObject { ["amount"] = 1, ["from"] = "usd" }));
        Assert.Equal("KRW", dflt!["result"]!["structuredContent"]!["to"]!.GetValue<string>());
        var bad = await server.HandleAsync(Call(new JsonObject { ["amount"] = 1, ["from"] = "zzz" }));
        Assert.True(bad!["result"]!["isError"]!.GetValue<bool>());
        var badAmount = await server.HandleAsync(Call(new JsonObject { ["amount"] = "many", ["from"] = "usd" }));
        Assert.True(badAmount!["result"]!["isError"]!.GetValue<bool>());
    }

    internal sealed class FakeSource(RateTable? table) : IRateSource
    {
        public int Calls;
        public TimeSpan Delay = TimeSpan.Zero;

        public async Task<RateTable> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return table ?? throw new HttpRequestException("offline");
        }
    }
}
