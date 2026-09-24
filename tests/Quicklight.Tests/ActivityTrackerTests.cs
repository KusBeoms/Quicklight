using Quicklight.Core.Activity;
using Xunit;

namespace Quicklight.Tests;

public class ActivityTrackerTests
{
    [Fact]
    public void Begin_report_and_dispose()
    {
        var tracker = new ActivityTracker();
        int changes = 0;
        tracker.Changed += () => changes++;

        using (var a = tracker.Begin("a", "첫 번째", 0.2))
        {
            using var b = tracker.Begin("b", "두 번째");
            a.Report("첫 번째", 0.5, "detail");
            var snapshot = tracker.Snapshot();
            Assert.Equal(["a", "b"], snapshot.Select(s => s.Id));
            Assert.Equal(0.5, snapshot[0].Progress);
            Assert.Equal("detail", snapshot[0].Detail);
            Assert.Null(snapshot[1].Progress); // indeterminate
        }

        Assert.Empty(tracker.Snapshot());
        Assert.Equal(5, changes); // begin a, begin b, report a, end b, end a
    }

    [Fact]
    public void Progress_is_clamped_and_double_dispose_is_quiet()
    {
        var tracker = new ActivityTracker();
        var h = tracker.Begin("x", "x", 1.7);
        Assert.Equal(1, tracker.Snapshot()[0].Progress);
        h.Report("x", -3);
        Assert.Equal(0, tracker.Snapshot()[0].Progress);
        int changes = 0;
        tracker.Changed += () => changes++;
        h.Dispose();
        h.Dispose();
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Same_id_replaces_in_place()
    {
        var tracker = new ActivityTracker();
        tracker.Begin("a", "one");
        tracker.Begin("b", "two");
        tracker.Begin("a", "again");
        Assert.Equal(["again", "two"], tracker.Snapshot().Select(s => s.Title));
    }
}
