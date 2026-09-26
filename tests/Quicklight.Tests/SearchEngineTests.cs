using Quicklight.Core;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;

namespace Quicklight.Tests;

public class SearchEngineTests
{
    static readonly AppEntry[] FakeApps =
    [
        new("Visual Studio Code", @"C:\Users\u\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
        new("Visual Studio 2022", @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"),
        new("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
        new("카카오톡", @"C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe"),
        new("메모장", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"),
        new("계산기", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        new("Uninstall Chrome Helper", @"C:\Program Files\Helper\uninstall.exe"),
    ];

    static SearchEngine Create(UsageStore? usage = null) =>
        new(new QuicklightSettings(), usage ?? new UsageStore(null), new AppProvider(FakeApps));

    static async Task<SearchResult> Top(SearchEngine e, string q) => (await e.SearchAsync(q))[0];

    [Theory]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("code", "Visual Studio Code")]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("카톡", "카카오톡")]
    [InlineData("ㅋㅋㅇㅌ", "카카오톡")]
    [InlineData("zkzkdhxhr", "카카오톡")]
    [InlineData("ㅊㅗ개ㅡㄷ", "Google Chrome")]
    [InlineData("메모", "메모장")]
    public async Task Top_hit_without_prefixes(string q, string expected) => Assert.Equal(expected, (await Top(Create(), q)).Title);

    [Fact]
    public async Task Calculator_wins_for_math()
    {
        var top = await Top(Create(), "12*(3+4)");
        Assert.Equal(ResultKind.Calculator, top.Kind);
        Assert.Equal("84", top.Title);
    }

    [Fact]
    public async Task Url_wins_for_urls() => Assert.Equal(ResultKind.Url, (await Top(Create(), "github.com")).Kind);

    [Fact]
    public async Task Settings_by_korean_or_english()
    {
        Assert.Equal("디스플레이", (await Top(Create(), "해상도")).Title);
        Assert.Equal("Bluetooth 및 장치", (await Top(Create(), "블루투스")).Title);
        Assert.Equal("환경 변수", (await Top(Create(), "환경 변수")).Title);
    }

    [Fact]
    public async Task Web_search_is_always_last()
    {
        var r = await Create().SearchAsync("chrome");
        Assert.Equal(ResultKind.WebSearch, r[^1].Kind);
        var none = await Create().SearchAsync("qwpeoiruty");
        Assert.Equal(ResultKind.WebSearch, none[0].Kind);
    }

    [Fact]
    public async Task Destructive_commands_need_a_near_full_name()
    {
        var r = await Create().SearchAsync("종");
        Assert.DoesNotContain(r, x => x.Target == "shutdown");
        var full = await Create().SearchAsync("시스템 종료");
        Assert.Equal("shutdown", full[0].Target);
        Assert.True(full[0].RequiresConfirmation);
    }

    [Fact]
    public async Task Noise_entries_rank_below_the_real_app()
    {
        var r = await Create().SearchAsync("chrome");
        Assert.True(r.FindIndex(x => x.Title == "Google Chrome") < r.FindIndex(x => x.Title == "Uninstall Chrome Helper"));
    }

    [Fact]
    public async Task Learns_from_selection()
    {
        var usage = new UsageStore(null);
        var engine = Create(usage);
        var before = await engine.SearchAsync("visual");
        // Whichever Visual Studio is not on top gets picked twice and must move up.
        var picked = before.Where(x => x.Title.StartsWith("Visual Studio")).Skip(1).First();
        engine.RecordSelection("visual", picked);
        engine.RecordSelection("visual", picked);
        Assert.Equal(picked.Title, (await engine.SearchAsync("visual"))[0].Title);
        Assert.Equal(picked.Title, (await engine.SearchAsync("vis"))[0].Title); // prefix of the learned query
    }

    [Fact]
    public async Task Limit_is_respected_exactly()
    {
        var one = await Create().SearchAsync("chrome", maxResults: 1);
        Assert.Equal("Google Chrome", Assert.Single(one).Title); // not just the web search row
        var three = await Create().SearchAsync("visual", maxResults: 3);
        Assert.Equal(3, three.Count);
        Assert.Equal(ResultKind.WebSearch, three[^1].Kind);
        var withWeb = await Create().SearchAsync("chrome", maxResults: 2, kinds: new HashSet<ResultKind> { ResultKind.App, ResultKind.WebSearch });
        Assert.Equal(2, withWeb.Count);
    }

    [Fact]
    public async Task Kind_filter()
    {
        var r = await Create().SearchAsync("chrome", kinds: new HashSet<ResultKind> { ResultKind.App });
        Assert.All(r, x => Assert.Equal(ResultKind.App, x.Kind));
    }

    [Fact]
    public void Usage_store_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql-history-{Guid.NewGuid():N}.json");
        try
        {
            var store = new UsageStore(path);
            store.Record("App:x", 2, 80);
            store.Flush();
            var reloaded = new UsageStore(path);
            Assert.True(reloaded.Boost("App:x", 1, () => 90) > reloaded.Boost("App:x", 1, () => 40)); // a weak match learns nothing
            Assert.Equal(0, reloaded.Boost("App:y", 1, () => 90));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void History_keeps_results_not_queries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql-history-{Guid.NewGuid():N}.json");
        try
        {
            // The old format stored the query itself; loading rewrites it without.
            File.WriteAllText(path, """[{"Query":"secret plan","Key":"fs:c:\\x.txt","When":"2026-01-01T00:00:00"}]""");
            var store = new UsageStore(path);
            store.Flush();
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("secret", json);
            Assert.Contains("x.txt", json);

            var engine = new SearchEngine(new QuicklightSettings(), store, new AppProvider(FakeApps));
            engine.RecordSelection("kakao private words", new SearchResult { Title = "카카오톡", Kind = ResultKind.App, Target = "k" });
            store.Flush();
            Assert.DoesNotContain("private", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Advanced_search_off_uses_builtin_values_and_keeps_its_own()
    {
        var s = new QuicklightSettings { MaxResults = 3, ExcludedPaths = [@"\secret\"], DemotedPaths = [], AdvancedSearch = false };
        Assert.Equal(new QuicklightSettings().MaxResults, s.ResultLimit);
        Assert.Empty(s.ExcludedPathsInEffect);
        Assert.Equal(new QuicklightSettings().DemotedPaths, s.DemotedPathsInEffect);
        Assert.Equal(3, s.MaxResults); // remembered
        s.AdvancedSearch = true;
        Assert.Equal(3, s.ResultLimit);
        Assert.Equal([@"\secret\"], s.ExcludedPathsInEffect);
    }

    [Fact]
    public void Recent_queries_keep_the_last_ten_newest_first()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql-recent-{Guid.NewGuid():N}.json");
        try
        {
            var recent = new RecentQueries(path);
            for (int i = 1; i <= 12; i++) recent.Add($"q{i}");
            recent.Add(" q5 "); // again: moves to the front, no duplicate
            recent.Add("");
            var reloaded = new RecentQueries(path);
            Assert.Equal(["q5", "q12", "q11", "q10", "q9", "q8", "q7", "q6", "q4", "q3"], reloaded.Items);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Files_that_belong_to_a_listed_app_fold_into_it()
    {
        const string exe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var files = new FixedProvider(
            new SearchResult { Title = "chrome.exe", Kind = ResultKind.File, Target = exe, Score = 50 },
            new SearchResult { Title = "chrome notes.txt", Kind = ResultKind.File, Target = @"C:\chrome notes.txt", Score = 50 });
        var engine = new SearchEngine(new QuicklightSettings(), new UsageStore(null), new AppProvider(FakeApps), extraProviders: [files]);
        var r = await engine.SearchAsync("chrome");
        Assert.Single(r, x => x.Title == "Google Chrome");
        Assert.DoesNotContain(r, x => x.Kind == ResultKind.File && x.Target == exe);
        Assert.Contains(r, x => x.Title == "chrome notes.txt");
    }

    sealed class FixedProvider(params SearchResult[] results) : IResultProvider
    {
        public string Name => "fixed";
        public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(query.IsAlternate ? [] : results);
    }
}

static class ListExtensions
{
    public static int FindIndex<T>(this IReadOnlyList<T> list, Func<T, bool> pred)
    {
        for (int i = 0; i < list.Count; i++) if (pred(list[i])) return i;
        return -1;
    }
}
