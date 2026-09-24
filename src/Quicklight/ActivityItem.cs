using System.ComponentModel;
using Quicklight.Core.Activity;

namespace Quicklight;

/// <summary>One progress bar under the results: an <see cref="ActivityInfo"/>, updated in place so the bar animates.</summary>
public sealed class ActivityItem(string id) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public string Title { get; private set; } = "";
    public string RightText { get; private set; } = "";
    public double Progress { get; private set; }
    public bool IsIndeterminate { get; private set; }

    public void Update(ActivityInfo info)
    {
        Title = info.Title;
        IsIndeterminate = info.Progress is null;
        Progress = info.Progress ?? 0;
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(info.Detail)) parts.Add(info.Detail);
        if (info.Progress is { } p) parts.Add(p.ToString("P0"));
        RightText = string.Join("   ·   ", parts);
        foreach (var name in new[] { nameof(Title), nameof(RightText), nameof(Progress), nameof(IsIndeterminate) }) Changed(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
