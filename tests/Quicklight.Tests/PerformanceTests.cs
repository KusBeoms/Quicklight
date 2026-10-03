using System.Diagnostics;
using Quicklight.Core;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Xunit.Abstractions;

namespace Quicklight.Tests;

public class PerformanceTests(ITestOutputHelper output)
{
    static readonly string[] Words =
    [
        "Visual", "Studio", "Code", "Google", "Chrome", "Microsoft", "Office", "Adobe", "Photoshop", "Player", "Media", "Editor",
        "Manager", "Studio", "Cloud", "Drive", "Sync", "Remote", "Desktop", "Terminal", "Notes", "Mail", "Music", "Video",
        "카카오톡", "네이버", "한글", "메모장", "계산기", "그림판", "탐색기", "설정",
    ];

    /// <summary>Apps with made-up paths that do not exist, like a machine with a few hundred installed programs.</summary>
    static List<AppEntry> SyntheticApps(int count)
    {
        var rng = new Random(1);
        var apps = new List<AppEntry>();
        for (int i = 0; i < count; i++)
        {
            var name = $"{Words[rng.Next(Words.Length)]} {Words[rng.Next(Words.Length)]} {i}";
            apps.Add(new AppEntry(name, [
                new AppPart(AppPartKind.Shortcut, $@"C:\ql-perf-missing\Menu\{name}.lnk"),
                new AppPart(AppPartKind.Exe, $@"C:\ql-perf-missing\App{i}\app{i}.exe"),
                new AppPart(AppPartKind.Uninstaller, $@"C:\ql-perf-missing\App{i}\uninstall.exe"),
            ]));
        }
        return apps;
    }

    /// <summary>
    /// Typing speed of the whole engine, one keystroke at a time. Only runs with QL_PERF=1 (it measures, it does not
    /// assert): <c>set QL_PERF=1 &amp;&amp; dotnet test --filter PerformanceTests</c>.
    /// </summary>
    [Fact]
    public async Task Typing_over_many_apps_and_a_full_history()
    {
        if (Environment.GetEnvironmentVariable("QL_PERF") != "1") return;
        var apps = SyntheticApps(500);
        var usage = new UsageStore(null);
        var rng = new Random(2);
        var start = DateTime.Now.AddDays(-60);
        for (int i = 0; i < 3000; i++)
            usage.Record(AppProvider.ToResult(apps[rng.Next(apps.Count)]).Key, rng.Next(1, 8), rng.Next(50, 100), start.AddMinutes(i * 20));
        var engine = new SearchEngine(new QuicklightSettings(), usage, new AppProvider(apps));

        foreach (var text in new[] { "visual studio", "zkzkdhxhr", "ㅋㅋㅇㅌ", "ㅇㅍㅁㅈ", "photoshp" })
        {
            await engine.SearchAsync(text, files: FileStage.None); // warm up
            var sw = Stopwatch.StartNew();
            for (int round = 0; round < 3; round++)
                for (int n = 1; n <= text.Length; n++)
                    await engine.SearchAsync(text[..n], files: FileStage.None);
            output.WriteLine($"{text,-14} {sw.Elapsed.TotalMilliseconds / (3 * text.Length),8:0.00} ms/keystroke");
        }
    }
}
