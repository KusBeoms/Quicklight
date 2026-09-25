using Quicklight.Core;
using Quicklight.Core.Matching;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;

namespace Quicklight.Tests;

public class JamoOrderTests
{
    [Theory]
    [InlineData("ㅏㅈ", "자")]
    [InlineData("ㅏㅋ카오톡", "카카오톡")]
    [InlineData("카카ㅗㅇ톡", "카카오톡")]
    [InlineData("ㅔㅁ모장", "메모장")]
    [InlineData("ㅓㅁ스ㅔㅌ", "머스테")] // every swapped pair, not only the first
    public void Swaps_vowel_typed_before_its_consonant(string typed, string expected) =>
        Assert.Equal(expected, Hangul.FixJamoOrder(typed));

    [Theory]
    [InlineData("카카오톡")]
    [InlineData("ㅋㅋㅇㅌ")]
    [InlineData("abc")]
    [InlineData("ㅏ")]
    public void Leaves_normal_input_alone(string typed) => Assert.Null(Hangul.FixJamoOrder(typed));

    [Fact]
    public async Task Search_finds_the_app_despite_the_typo()
    {
        var engine = new SearchEngine(new QuicklightSettings(), new UsageStore(null),
            new AppProvider([new AppEntry("카카오톡", @"C:\Kakao\KakaoTalk.exe"), new AppEntry("메모장", "notepad!App")]));
        Assert.Equal("카카오톡", (await engine.SearchAsync("ㅏㅋ카오톡"))[0].Title);
    }
}

public class UnitTests
{
    [Theory]
    [InlineData("5km in mile", "3.106856 mile")]
    [InlineData("70kg lb", "154.323584 lb")]
    [InlineData("100c f", "212 °F")]
    [InlineData("32 °f to c", "0 °C")]
    [InlineData("1gb mb", "1,024 MB")]
    [InlineData("84m2 평", "25.41 평")]
    [InlineData("1,000 m → km", "1 km")]
    public void Converts(string q, string expected) => Assert.Equal(expected, UnitProvider.Convert(q)[0].Title);

    [Fact]
    public void Without_target_shows_counterparts()
    {
        var r = UnitProvider.Convert("10 km");
        Assert.Equal("6.213712 mile", r[0].Title);
        Assert.Equal("6.213712", r[0].Target);
    }

    [Theory]
    [InlineData("5km in kg")]    // different categories
    [InlineData("hello world")]
    [InlineData("5 apples")]
    [InlineData("vscode")]
    public void Ignores_non_conversions(string q) => Assert.Empty(UnitProvider.Convert(q));
}

public class DateTests
{
    static readonly DateTime Now = new(2026, 9, 25, 15, 0, 0);

    [Theory]
    [InlineData("오늘 +100일", "2027-01-03")]
    [InlineData("today +2w", "2026-10-09")]
    [InlineData("오늘 -1개월", "2026-08-25")]
    [InlineData("d-day 2026-12-25", "D-91")]
    [InlineData("2026-09-20까지", "D+5")]
    [InlineData("12월 25일까지", "D-91")]
    [InlineData("d-day 9/25", "D-Day")]
    [InlineData("2월 29일까지", "D-522")] // 2026 has none: the next is 2028-02-29
    public void Answers(string q, string copied) => Assert.Equal(copied, DateProvider.Evaluate(q, Now)[0].Target);

    [Fact]
    public void Feb_29_after_it_passed_in_a_leap_year() =>
        Assert.StartsWith("2032년 2월 29일", DateProvider.Evaluate("2월 29일까지", new DateTime(2028, 3, 1))[0].Subtitle);

    [Fact]
    public void City_time() => Assert.Contains("여기와", DateProvider.Evaluate("도쿄 시간", Now)[0].Subtitle);

    [Theory]
    [InlineData("today")]
    [InlineData("시간")]
    [InlineData("아무개 시간")]
    public void Ignores_others(string q) => Assert.Empty(DateProvider.Evaluate(q, Now));
}

public class KeywordSearchTests
{
    [Fact]
    public void Keyword_picks_the_engine()
    {
        var r = WebSearchProvider.MakeKeyword("yt 고양이 영상", new QuicklightSettings())!;
        Assert.StartsWith("https://www.youtube.com/results?search_query=", r.Target);
        Assert.EndsWith(Uri.EscapeDataString("고양이 영상"), r.Target);
    }

    [Theory]
    [InlineData("yt")]
    [InlineData("youtube 고양이")]
    [InlineData("고양이")]
    public void No_keyword(string q) => Assert.Null(WebSearchProvider.MakeKeyword(q, new QuicklightSettings()));

    [Fact]
    public async Task Keyword_result_ranks_first()
    {
        var engine = new SearchEngine(new QuicklightSettings(), new UsageStore(null), new AppProvider([]));
        var top = (await engine.SearchAsync("nv 날씨"))[0];
        Assert.Contains("search.naver.com", top.Target);
    }

    [Fact]
    public async Task App_named_like_the_query_beats_the_keyword()
    {
        var app = new AppEntry("G HUB", @"C:\Program Files\LGHUB\lghub.exe");
        var engine = new SearchEngine(new QuicklightSettings { SearchEngines = { ["g"] = "https://www.google.com/search?q={0}" } },
            new UsageStore(null), new AppProvider([app]));
        Assert.Equal("G HUB", (await engine.SearchAsync("g hub"))[0].Title);
    }
}

public class SnippetAndCommandTests
{
    [Fact]
    public async Task Snippet_key_pastes_text_and_command_runs_program()
    {
        var settings = new QuicklightSettings
        {
            Snippets = { [";addr"] = "서울시 중구 세종대로 110" },
            Commands = [new CustomCommand("빌드", @"C:\tools\build.cmd", "--release", ["build"])],
        };
        var engine = new SearchEngine(settings, new UsageStore(null), new AppProvider([]));

        var snippet = (await engine.SearchAsync(";addr"))[0];
        Assert.Equal((ResultKind.Snippet, ActionType.Paste, "서울시 중구 세종대로 110"), (snippet.Kind, snippet.Action, snippet.Target));

        var cmd = (await engine.SearchAsync("build"))[0];
        Assert.Equal((@"C:\tools\build.cmd", "--release"), (cmd.Target, cmd.Arguments));
    }

    [Fact]
    public async Task Commands_with_the_same_program_both_show()
    {
        var settings = new QuicklightSettings
        {
            Commands = [new CustomCommand("deploy dev", @"C:\tools\deploy.cmd", "dev"), new CustomCommand("deploy prod", @"C:\tools\deploy.cmd", "prod")],
        };
        var engine = new SearchEngine(settings, new UsageStore(null), new AppProvider([]));
        var args = (await engine.SearchAsync("deploy")).Where(r => r.Kind == ResultKind.Command).Select(r => r.Arguments).ToHashSet();
        Assert.Equal(["dev", "prod"], args.Order());
    }
}
