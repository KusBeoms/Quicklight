namespace Quicklight.Core.Activity;

/// <param name="Progress">0..1, or null when the amount of work is unknown (shown as a moving bar).</param>
public sealed record ActivityInfo(string Id, string Title, double? Progress, string? Detail);

/// <summary>
/// Long-running background work the launcher shows as progress bars: translation engine install, language models,
/// update downloads, app and file indexing. Thread-safe; <see cref="Changed"/> fires on the reporting thread.
/// </summary>
public sealed class ActivityTracker
{
    public static ActivityTracker Shared { get; } = new();

    readonly object _gate = new();
    readonly Dictionary<string, ActivityInfo> _items = [];
    readonly List<string> _order = [];

    public event Action? Changed;

    /// <summary>Starts (or replaces) an activity; dispose the handle when it ends.</summary>
    public Handle Begin(string id, string title, double? progress = null, string? detail = null)
    {
        Set(new ActivityInfo(id, title, progress, detail));
        return new Handle(this, id);
    }

    public IReadOnlyList<ActivityInfo> Snapshot()
    {
        lock (_gate) return _order.Select(id => _items[id]).ToList();
    }

    void Set(ActivityInfo info)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(info.Id)) _order.Add(info.Id);
            _items[info.Id] = info with { Progress = info.Progress is { } p ? Math.Clamp(p, 0, 1) : null };
        }
        Changed?.Invoke();
    }

    void End(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.Remove(id);
            _order.Remove(id);
        }
        if (removed) Changed?.Invoke();
    }

    public sealed class Handle(ActivityTracker tracker, string id) : IDisposable
    {
        public string Id => id;

        public void Report(string title, double? progress = null, string? detail = null) =>
            tracker.Set(new ActivityInfo(id, title, progress, detail));

        public void Dispose() => tracker.End(id);
    }
}
