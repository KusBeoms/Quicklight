using System.Text.Json;

namespace Quicklight.Core;

/// <summary>
/// Remembers what the user opened for which query, so results learn like Spotlight:
/// the item picked for "vs" is boosted again for "v", "vs" and "vsc", and frequently/recently used items rank higher overall.
/// </summary>
public sealed class UsageStore
{
    public sealed record Entry(string Query, string Key, DateTime When);

    const int MaxEntries = 3000;
    static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

    readonly string? _path;
    readonly object _gate = new();
    List<Entry> _entries = [];

    public UsageStore(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            _entries = (JsonSerializer.Deserialize<List<Entry?>>(File.ReadAllText(path)) ?? [])
                .Where(e => e is { Query: not null, Key: not null }).Select(e => e!).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { Log.Error("history load failed", ex); }
    }

    public static string DefaultPath => Path.Combine(QuicklightSettings.Directory, "history.json");

    public void Record(string query, string key, DateTime? when = null)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(Normalize(query), key, when ?? DateTime.Now));
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        ScheduleSave();
    }

    int _saveQueued;

    /// <summary>Writes on a worker thread so launching an item never waits for the disk; bursts coalesce into one write.</summary>
    void ScheduleSave()
    {
        if (_path is null || Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        Task.Run(() =>
        {
            Interlocked.Exchange(ref _saveQueued, 0);
            Save();
        });
    }

    /// <summary>Writes the history now, on the calling thread (tests, shutdown).</summary>
    public void Flush() => Save();

    /// <summary>0..~150. Query-specific learning dominates; general frecency is a smaller tie-breaker.</summary>
    public double Boost(string query, string key, DateTime? now = null)
    {
        var q = Normalize(query);
        var t = now ?? DateTime.Now;
        double frecency = 0, learned = 0;
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (e.Key != key) continue;
                double decay = Math.Pow(0.5, (t - e.When).TotalDays / HalfLife.TotalDays);
                frecency += decay;
                if (q.Length > 0 && e.Query.StartsWith(q, StringComparison.Ordinal))
                {
                    // Exact same query counts double; shorter prefixes count proportionally.
                    double closeness = e.Query == q ? 2.0 : (double)q.Length / e.Query.Length;
                    learned += decay * closeness;
                }
            }
        }
        return Math.Min(40, 12 * Math.Log2(1 + frecency)) + Math.Min(110, 45 * Math.Log2(1 + learned));
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        ScheduleSave();
    }

    static string Normalize(string q) => q.Trim().ToLowerInvariant();

    readonly object _fileGate = new();

    void Save()
    {
        if (_path is null) return;
        try
        {
            // Snapshot inside the file lock, so a slower earlier save can never overwrite a newer one.
            lock (_fileGate)
            {
                string json;
                lock (_gate) json = JsonSerializer.Serialize(_entries);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("history save failed", ex); }
    }
}
