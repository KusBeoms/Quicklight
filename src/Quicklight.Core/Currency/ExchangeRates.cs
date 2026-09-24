using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quicklight.Core.Currency;

/// <summary>Rates relative to <see cref="Base"/> (1 Base = Rates[code] code).</summary>
public sealed record RateTable(string Base, IReadOnlyDictionary<string, double> Rates, DateTime UpdatedUtc, string Source)
{
    public bool Has(string code) => Rates.ContainsKey(code);

    public double Convert(double amount, string from, string to) => amount / Rates[from] * Rates[to];

    /// <summary>How many <paramref name="to"/> one <paramref name="from"/> buys.</summary>
    public double Rate(string from, string to) => Rates[to] / Rates[from];
}

public interface IRateSource
{
    Task<RateTable> FetchAsync(CancellationToken ct);
}

/// <summary>
/// Free, key-less daily rates: open.er-api.com (≈166 currencies) with the ECB-based Frankfurter API as fallback.
/// Only the rate table is downloaded; nothing the user typed is sent.
/// </summary>
public sealed class HttpRateSource : IRateSource
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        // A rate table is ~5 KB; anything past 1 MB is not one.
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 1 << 20 };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Quicklight/0.1 (+https://github.com)");
        return c;
    }

    public async Task<RateTable> FetchAsync(CancellationToken ct)
    {
        try { return ParseOpenErApi(await Http.GetStringAsync("https://open.er-api.com/v6/latest/USD", ct).ConfigureAwait(false)); }
        // Any failure of the first source (network, bad JSON, missing fields) falls through to the second.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error("open.er-api.com failed, trying Frankfurter", ex);
        }
        return ParseFrankfurter(await Http.GetStringAsync("https://api.frankfurter.app/latest?from=USD", ct).ConfigureAwait(false));
    }

    internal static RateTable ParseOpenErApi(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("not a JSON object");
        if (Str(o["result"]) != "success") throw new FormatException("open.er-api.com: " + o["error-type"]);
        var rates = ReadRates(o["rates"]);
        if (o["time_last_update_unix"] is not JsonValue t || !t.TryGetValue<long>(out var unix) || unix is < 0 or > 32503680000)
            throw new FormatException("bad update time");
        var baseCode = Str(o["base_code"]) ?? throw new FormatException("no base currency");
        if (!rates.ContainsKey(baseCode)) rates[baseCode] = 1;
        return new RateTable(baseCode.ToUpperInvariant(), rates, DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, "open.er-api.com");
    }

    internal static RateTable ParseFrankfurter(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("not a JSON object");
        var baseCode = Str(o["base"]) ?? throw new FormatException("no base currency");
        var rates = ReadRates(o["rates"]);
        rates[baseCode] = 1; // Frankfurter leaves the base out
        if (!DateTime.TryParse(Str(o["date"]), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date))
            throw new FormatException("bad date");
        return new RateTable(baseCode.ToUpperInvariant(), rates, date, "frankfurter.app (ECB)");
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static Dictionary<string, double> ReadRates(JsonNode? node)
    {
        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject obj) throw new FormatException("no rates");
        foreach (var (code, value) in obj)
            // Positive, finite and not absurd (the smallest real rates are ~1e-5, the largest ~1e6 per USD).
            if (code.Length == 3 && value is JsonValue v && v.TryGetValue<double>(out var r) && double.IsFinite(r) && r is > 1e-9 and < 1e12)
                rates[code.ToUpperInvariant()] = r;
        if (rates.Count < 10) throw new FormatException("too few rates");
        return rates;
    }
}

/// <summary>
/// Holds the current rate table, cached on disk. Reads never wait for the network: a stale or missing table
/// triggers one background refresh; failures wait a while before retrying.
/// </summary>
public sealed class ExchangeRateStore
{
    public static string DefaultCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "rates.json");

    readonly IRateSource _source;
    readonly string? _cachePath;
    readonly TimeSpan _maxAge;
    readonly TimeSpan _retryAfterFailure;
    readonly Func<DateTime> _utcNow;
    readonly object _gate = new();
    Task<RateTable?>? _refresh;
    DateTime _lastFailureUtc = DateTime.MinValue; // guarded by _gate

    /// <summary>The table and when it was downloaded, published together so readers never see a mix of two refreshes.</summary>
    sealed record Snapshot(RateTable Table, DateTime FetchedUtc);
    volatile Snapshot? _snapshot;

    public RateTable? Current => _snapshot?.Table;

    public ExchangeRateStore(IRateSource source, string? cachePath, TimeSpan? maxAge = null, TimeSpan? retryAfterFailure = null, Func<DateTime>? utcNow = null)
    {
        _source = source;
        _cachePath = cachePath;
        _maxAge = maxAge ?? TimeSpan.FromHours(6);
        _retryAfterFailure = retryAfterFailure ?? TimeSpan.FromMinutes(10);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        LoadCache();
    }

    // A download time in the future (the clock was changed) also counts as stale.
    public bool IsStale => _snapshot is not { } s || _utcNow() - s.FetchedUtc is var age && (age > _maxAge || age < -TimeSpan.FromDays(1));

    /// <summary>Starts a refresh if the table is missing or old and none is running. Returns immediately.</summary>
    public void RefreshInBackgroundIfStale()
    {
        if (!IsStale) return;
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false }) return;
            if (_utcNow() - _lastFailureUtc < _retryAfterFailure) return;
            // Task.Run: even the synchronous start of an HTTP request (proxy auto-detection) must stay off the caller's thread.
            _refresh = Task.Run(RefreshAsync);
        }
    }

    /// <summary>For explicit requests (MCP): refresh if stale, waiting up to <paramref name="timeout"/>; falls back to the cached table.</summary>
    public async Task<RateTable?> GetAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        RefreshInBackgroundIfStale();
        Task<RateTable?>? pending;
        lock (_gate) pending = _refresh;
        if (pending is { IsCompleted: false })
            await Task.WhenAny(pending, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return Current;
    }

    async Task<RateTable?> RefreshAsync()
    {
        try
        {
            var table = await _source.FetchAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshot = new Snapshot(table, _utcNow());
            _snapshot = snapshot;
            SaveCache(snapshot);
            Log.Info($"exchange rates updated from {table.Source} ({table.Rates.Count} currencies, {table.UpdatedUtc:yyyy-MM-dd})");
            return table;
        }
        catch (Exception ex)
        {
            lock (_gate) _lastFailureUtc = _utcNow();
            Log.Error("exchange rate refresh failed", ex);
            return null;
        }
    }

    sealed record CacheFile(string Base, Dictionary<string, double> Rates, DateTime UpdatedUtc, string Source, DateTime FetchedUtc);

    void LoadCache()
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return;
        try
        {
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath));
            if (c?.Rates is not { Count: > 0 } || c.Base is null) return;
            // The cache is re-validated like a download: a tampered file cannot inject NaN, zero or negative rates.
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (code, rate) in c.Rates) // indexer, not ToDictionary: "USD" and "usd" must not throw
                if (code is { Length: 3 } && double.IsFinite(rate) && rate is > 1e-9 and < 1e12) rates[code.ToUpperInvariant()] = rate;
            if (rates.Count < 10) return;
            if (!rates.ContainsKey(c.Base)) rates[c.Base] = 1;
            _snapshot = new Snapshot(new RateTable(c.Base.ToUpperInvariant(), rates, c.UpdatedUtc, c.Source ?? "cache"), c.FetchedUtc);
        }
        catch (Exception ex) { Log.Error("rate cache load failed", ex); } // a broken cache must never stop the app from starting
    }

    void SaveCache(Snapshot s)
    {
        if (_cachePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            // Per-process temp name: the launcher and the MCP server may save at the same moment.
            var tmp = $"{_cachePath}.{Environment.ProcessId}.tmp";
            var t = s.Table;
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile(t.Base, new(t.Rates), t.UpdatedUtc, t.Source, s.FetchedUtc)));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("rate cache save failed", ex); }
    }
}
