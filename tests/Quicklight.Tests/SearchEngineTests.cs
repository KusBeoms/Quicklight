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
            store.Record("vs", "App:x");
            store.Flush();
            var reloaded = new UsageStore(path);
            Assert.True(reloaded.Boost("v", "App:x") > 0);
            Assert.Equal(0, reloaded.Boost("v", "App:y"));
        }
        finally { File.Delete(path); }
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
