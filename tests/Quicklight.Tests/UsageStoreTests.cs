using Quicklight.Core;

namespace Quicklight.Tests;

public class UsageStoreTests
{
    /// <summary>The plain scan over every entry that Boost is an index over; entries past 3000 drop the oldest.</summary>
    static double Reference(List<UsageStore.Entry> entries, string key, int typed, double strength, DateTime now)
    {
        double frecency = 0, learned = 0;
        foreach (var e in entries.Skip(Math.Max(0, entries.Count - 3000)))
        {
            if (e.Key != key) continue;
            double decay = Math.Pow(0.5, (now - e.When).TotalDays / 14);
            frecency += decay;
            if (typed == 0 || strength < e.Strength - 10) continue;
            double closeness = typed == e.Typed ? 2.0 : typed < e.Typed ? (double)typed / e.Typed : 1.0;
            learned += decay * closeness;
        }
        return Math.Min(40, 12 * Math.Log2(1 + frecency)) + Math.Min(110, 45 * Math.Log2(1 + learned));
    }

    [Fact]
    public void Boost_matches_a_full_scan_also_after_old_entries_drop_out()
    {
        var store = new UsageStore(null);
        var entries = new List<UsageStore.Entry>();
        var rng = new Random(3);
        var now = new DateTime(2026, 9, 1);
        string[] keys = ["App:a", "App:b", "App:c", "fs:c:\\x.txt"];
        for (int i = 0; i < 3500; i++)
        {
            var e = new UsageStore.Entry(keys[rng.Next(keys.Length)], now.AddMinutes(-i * 7), rng.Next(1, 6), rng.Next(40, 100));
            store.Record(e.Key, e.Typed, e.Strength, e.When);
            entries.Add(e);
            if (i % 700 != 0) continue;
            foreach (var key in keys)
                Assert.Equal(Reference(entries, key, 3, 80, now), store.Boost(key, 3, () => 80, now), 9);
        }
        foreach (var key in keys.Append("App:none"))
            foreach (var typed in new[] { 0, 1, 3, 9 })
                Assert.Equal(Reference(entries, key, typed, 70, now), store.Boost(key, typed, () => 70, now), 9);

        store.Clear();
        Assert.Equal(0, store.Boost("App:a", 3, () => 80, now));
    }
}
