using Quicklight.Core.Models;
using Quicklight.Core.Shell;

namespace Quicklight.Tests;

public class RecentFilesTests
{
    [Fact]
    public void Lists_existing_targets_newest_first_and_filters_by_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ql-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "Quarterly report.txt");
            File.WriteAllText(target, "x");
            var subfolder = Path.Combine(dir, "Photos");
            Directory.CreateDirectory(subfolder);
            var gone = Path.Combine(dir, "gone.txt"); // never created: skipped
            var old = Path.Combine(dir, "old.lnk");
            var fresh = Path.Combine(dir, "fresh.lnk");
            var stale = Path.Combine(dir, "stale.lnk");
            File.WriteAllText(old, ""); File.SetLastWriteTime(old, DateTime.Now.AddDays(-2));
            File.WriteAllText(fresh, ""); File.SetLastWriteTime(fresh, DateTime.Now.AddMinutes(-1));
            File.WriteAllText(stale, ""); File.SetLastWriteTime(stale, DateTime.Now.AddDays(-1));
            var targets = new Dictionary<string, string?>
            {
                [old] = target,
                [fresh] = subfolder,
                [stale] = gone,
            };

            var items = RecentFiles.FromShortcuts(dir, lnk => targets[lnk], p => false);

            Assert.Equal(["Photos", "Quarterly report.txt"], items.Select(i => i.Title).ToArray());
            Assert.Equal(ResultKind.Folder, items[0].Kind);
            Assert.Equal(ResultKind.File, items[1].Kind);
            Assert.Single(RecentFiles.Filter(items, "quarter"));
            Assert.Equal(2, RecentFiles.Filter(items, "").Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
