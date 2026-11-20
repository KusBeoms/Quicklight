using System.Text;
using System.Text.Json;

namespace Quicklight.Core.Ai;

/// <summary>
/// The AI conversations, kept in %APPDATA%\Quicklight\conversations.json (null path = memory only). Mutations may come
/// from any thread; saves are debounced and run on a worker thread. A conversation with no messages is never saved
/// and is not listed.
/// </summary>
public sealed class ConversationStore
{
    const int TitleLength = 30;
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    readonly string? _path;
    readonly object _gate = new();     // guards _items
    readonly object _saveGate = new(); // one write at a time
    readonly List<Conversation> _items = [];
    readonly Timer? _timer;

    public ConversationStore(string? path)
    {
        _path = path;
        if (path is null) return;
        _timer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        if (!File.Exists(path)) return;
        try
        {
            // Valid JSON can still hold nulls ([null], "Messages": null): such entries are dropped, not a crash on every start.
            _items = (JsonSerializer.Deserialize<List<Conversation?>>(File.ReadAllText(path)) ?? [])
                .Where(c => c is { Messages: { Count: > 0 } }).Select(c => c!).ToList();
            foreach (var c in _items) { c.Id ??= Guid.NewGuid().ToString("N"); c.Title ??= ""; c.Messages.RemoveAll(m => m is null); }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NullReferenceException)
        {
            Log.Error("conversations load failed", ex);
            try { File.Move(path, $"{path}.{DateTime.Now:yyyyMMddHHmmss}.bak", overwrite: true); } // keep what the user had (a later bad file must not overwrite this one); the next save starts a new file
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string DefaultPath => Path.Combine(QuicklightSettings.Directory, "conversations.json");

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event Action? Changed;

    /// <summary>Pinned first, then the most recently updated; conversations without messages are left out.</summary>
    public IReadOnlyList<Conversation> All
    {
        get
        {
            lock (_gate) return _items.Where(c => c.Messages.Count > 0).OrderByDescending(c => c.Pinned).ThenByDescending(c => c.Updated).ToList();
        }
    }

    /// <summary>A copy of the messages, safe to enumerate while the AI thread adds to the conversation (the live list is not).</summary>
    public IReadOnlyList<ChatMessage> MessagesOf(Conversation c)
    {
        lock (_gate) return c.Messages.ToList();
    }

    public Conversation Create()
    {
        var c = new Conversation();
        lock (_gate) _items.Add(c);
        return c; // no Changed: it is not listed until it has a message
    }

    public Conversation? Get(string id)
    {
        lock (_gate) return _items.FirstOrDefault(c => c.Id == id);
    }

    public void Add(Conversation c, ChatMessage m)
    {
        lock (_gate)
        {
            // A conversation deleted while its answer was still being written must not come back with only that reply.
            if (!_items.Contains(c))
            {
                if (c.Messages.Count > 0) return;
                _items.Add(c);
            }
            c.Messages.Add(m);
            c.Updated = DateTime.Now;
            if (c.Title.Length == 0 && m.Role == ChatRole.User) c.Title = TitleOf(m);
        }
        Mutated();
    }

    public void Rename(string id, string title)
    {
        lock (_gate)
        {
            if (_items.FirstOrDefault(c => c.Id == id) is not { } c) return;
            c.Title = title.Trim();
        }
        Mutated();
    }

    public void SetPinned(string id, bool pinned)
    {
        lock (_gate)
        {
            if (_items.FirstOrDefault(c => c.Id == id) is not { } c) return;
            c.Pinned = pinned;
        }
        Mutated();
    }

    /// <returns>How many conversations were removed.</returns>
    public int Delete(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        int n;
        lock (_gate) n = _items.RemoveAll(c => set.Contains(c.Id));
        if (n > 0) Mutated();
        return n;
    }

    /// <summary>Removes conversations not updated for <paramref name="age"/>; pinned ones stay.</summary>
    public int PurgeOlderThan(TimeSpan age)
    {
        var limit = DateTime.Now - age;
        int n;
        lock (_gate) n = _items.RemoveAll(c => !c.Pinned && c.Updated < limit);
        if (n > 0) Mutated();
        return n;
    }

    /// <summary>Writes what is pending now and waits for it (tests, shutdown).</summary>
    public void Flush()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        Save();
    }

    static string TitleOf(ChatMessage m)
    {
        var line = m.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? m.Attachments.FirstOrDefault()?.Name ?? (m.Attachments.Count > 0 ? Path.GetFileName(m.Attachments[0].Path) : "");
        return line.Length > TitleLength ? line[..TitleLength].TrimEnd() + "…" : line;
    }

    void Mutated()
    {
        _timer?.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        Changed?.Invoke();
    }

    void Save()
    {
        if (_path is null) return;
        lock (_saveGate)
        {
            try
            {
                string json;
                lock (_gate) json = JsonSerializer.Serialize(_items.Where(c => c.Messages.Count > 0).ToList());
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp"; // a crash mid-write must not leave half a file
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    f.Write(new UTF8Encoding(false).GetBytes(json));
                    f.Flush(flushToDisk: true); // a power cut must not leave a zero-length file behind the rename
                }
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("conversations save failed", ex); }
        }
    }
}
