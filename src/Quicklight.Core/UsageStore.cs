using System.Text.Json;

namespace Quicklight.Core;

/// <summary>
/// Remembers what the user opened, so results learn like Spotlight: an item picked often or recently ranks higher,
/// and most of all for queries like the one it was picked with. Only the result is stored, never the query: how many
/// characters were typed and how well they matched the result's name stand in for it.
/// </summary>
public sealed class UsageStore
{
    /// <param name="Typed">Length of the query the result was picked with.</param>
    /// <param name="Strength">How well that query matched the result's name (FuzzyMatcher, 0-100).</param>
    public sealed record Entry(string Key, DateTime When, int Typed, double Strength);

    // What history.json held before: the query itself. Read once and rewritten without it.
    sealed record Stored(string? Key, DateTime When, int? Typed, double? Strength, string? Query);

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
            var stored = (JsonSerializer.Deserialize<List<Stored?>>(File.ReadAllText(path)) ?? []).Where(e => e?.Key is not null).Select(e => e!).ToList();
            _entries = stored.Select(e => new Entry(e.Key!, e.When, e.Typed ?? e.Query?.Trim().Length ?? 0, e.Strength ?? 0)).ToList();
            if (stored.Exists(e => e.Query is not null)) ScheduleSave(); // drop the old queries from disk
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { Log.Error("history load failed", ex); }
    }

    public static string DefaultPath => Path.Combine(QuicklightSettings.Directory, "history.json");

    public void Record(string key, int typed, double strength, DateTime? when = null)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(key, when ?? DateTime.Now, typed, strength));
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

    /// <summary>
    /// 0..~150. Learning dominates: picks made with about as much typed, when the current query matches the result
    /// at least about as well as it did then. General frecency is a smaller tie-breaker.
    /// </summary>
    /// <param name="typed">Length of the current query.</param>
    /// <param name="strength">How well the current query matches the result's name; only asked for when the key has history.</param>
    public double Boost(string key, int typed, Func<double> strength, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        double frecency = 0, learned = 0, match = -1;
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (e.Key != key) continue;
                double decay = Math.Pow(0.5, (t - e.When).TotalDays / HalfLife.TotalDays);
                frecency += decay;
                // A weaker match than back then is a different query (Chrome picked for "ch" says nothing about "cm").
                if (match < 0) match = strength();
                if (typed == 0 || match < e.Strength - 10) continue;
                // As much typed as then counts double; less counts in proportion, more counts once.
                double closeness = typed == e.Typed ? 2.0 : typed < e.Typed ? (double)typed / e.Typed : 1.0;
                learned += decay * closeness;
            }
        }
        return Math.Min(40, 12 * Math.Log2(1 + frecency)) + Math.Min(110, 45 * Math.Log2(1 + learned));
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        ScheduleSave();
    }

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
