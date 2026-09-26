using System.Text.Json;

namespace Quicklight.Core;

/// <summary>
/// The last few queries the user acted on (opened, ran, copied a result of), newest first, for ↑/↓ in an empty
/// search box like a terminal's history. Nothing else keeps queries.
/// </summary>
public sealed class RecentQueries
{
    public const int Max = 10;

    readonly string? _path;
    readonly List<string> _items = [];

    public RecentQueries(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try { _items = (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []).Where(q => q.Length > 0).Take(Max).ToList(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { Log.Error("recent queries load failed", ex); }
    }

    public static string DefaultPath => Path.Combine(QuicklightSettings.Directory, "recent.json");

    /// <summary>Newest first.</summary>
    public IReadOnlyList<string> Items => _items;

    public void Add(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return;
        _items.Remove(q); // the same query again moves to the front
        _items.Insert(0, q);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items)); // ten short strings: no need for a worker thread
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("recent queries save failed", ex); }
    }
}
