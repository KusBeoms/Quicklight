using System.Text.Json.Nodes;
using Quicklight.Core;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Quicklight.Core.Translation;
using Quicklight.Mcp;

namespace Quicklight.Tests;

public class TranslationTests
{
    [Theory]
    [InlineData("hello 번역", "hello", null)]
    [InlineData("hello 번역해줘", "hello", null)]
    [InlineData("번역 good morning", "good morning", null)]
    [InlineData("translate: bonjour", "bonjour", null)]
    [InlineData("사과 영어로", "사과", "en")]
    [InlineData("사과를 영어로 번역해줘", "사과", "en")]
    [InlineData("영어로 번역 오늘 날씨 좋다", "오늘 날씨 좋다", "en")]
    [InlineData("사과는 영어로", "사과", "en")]
    [InlineData("사과 영어로 뭐야?", "사과", "en")]
    [InlineData("serendipity의 뜻", "serendipity", null)]
    [InlineData("good morning in korean", "good morning", "ko")]
    [InlineData("translate thank you to japanese", "thank you", "ja")]
    [InlineData("감사합니다 일본어로", "감사합니다", "ja")]
    [InlineData("serendipity 뜻", "serendipity", null)]
    [InlineData("serendipity meaning", "serendipity", null)]
    [InlineData("\"hello world\" 번역", "hello world", null)]
    public void Parses(string query, string text, string? target)
    {
        var r = TranslationParser.Parse(query);
        Assert.NotNull(r);
        Assert.Equal(text, r!.Text);
        Assert.Equal(target, r.Target);
    }

    [Theory]
    [InlineData("자동번역")]          // an app name, not a request
    [InlineData("번역")]
    [InlineData("영어")]
    [InlineData("chrome")]
    [InlineData("100달러")]
    [InlineData("번역가 모집")]
    [InlineData("서울로 가는 길")]
    [InlineData("change language to korean")]
    [InlineData("go to english")]
    [InlineData("한국어로 설정")]
    [InlineData("한글로 문서 만들기")]
    [InlineData("삶의 의미")]
    [InlineData("영어로 번역")]
    public void Ignores(string query) => Assert.Null(TranslationParser.Parse(query));

    [Theory]
    [InlineData("사과", "ko", "en", "en")]        // already Korean on a Korean system → English
    [InlineData("apple", "ko", "en", "ko")]       // anything else → the system language
    [InlineData("ありがとう", "ko", "en", "ko")]
    [InlineData("hello", "en", "en", "en")]       // Latin text is left to the system language
    [InlineData("안녕", "en", "en", "en")]
    public void Default_target(string text, string system, string secondary, string expected) =>
        Assert.Equal(expected, TranslationParser.DefaultTarget(text, system, secondary));

    [Fact]
    public void System_language_without_a_model_falls_back()
    {
        string[] models = ["ko", "en", "ja", "zh"];
        Assert.Equal("en", TranslationParser.DefaultTarget("Guten Morgen", "de", "en", models)); // no German model
        Assert.Equal("ko", TranslationParser.Fallback("en", "en", models));                    // English text on an English system
        Assert.Equal("en", TranslationParser.Fallback("ko", "en", models));
    }

    [Fact]
    public async Task Text_already_in_the_target_is_translated_to_the_fallback()
    {
        var t = new FakeTranslator { DetectAs = "ko" }; // "detects" Korean, and the default target is ko
        var r = await Engine(t).SearchAsync("bonjour 번역", files: FileStage.Full);
        Assert.Equal("[en] bonjour", r[0].Title);
        Assert.Equal(2, t.Calls);
    }

    [Fact]
    public async Task Server_timeouts_become_a_row_and_keep_other_results()
    {
        var t = new FakeTranslator { Throw = new TaskCanceledException("timeout") };
        var r = await Engine(t).SearchAsync("hello 번역", files: FileStage.Full);
        Assert.Contains(r, x => x.Kind == ResultKind.Translation && x.Action == ActionType.None && x.Title.Contains("오래"));
        Assert.Equal(ResultKind.WebSearch, r[^1].Kind); // the stage still completed
    }

    [Fact]
    public void Script_guess()
    {
        Assert.Equal("ko", Languages.GuessByScript("오늘 날씨"));
        Assert.Equal("ja", Languages.GuessByScript("今日はいい天気"));
        Assert.Equal("zh", Languages.GuessByScript("今天天气很好"));
        Assert.Null(Languages.GuessByScript("hello"));
        Assert.Equal("중국어", Languages.KoreanName("zh-Hans"));
    }

    [Fact]
    public void Parses_server_responses()
    {
        var r = LibreTranslateClient.ParseResult("""
            {"alternatives":["Hello, today is good","Hello, today's weather is good"],"detectedLanguage":{"confidence":100.0,"language":"ko"},"translatedText":"Hello, today's weather is good"}
            """, "en");
        Assert.Equal("Hello, today's weather is good", r.Text);
        Assert.Equal("ko", r.Source);
        Assert.Equal(100, r.Confidence);
        Assert.Equal(["Hello, today is good"], r.Alternatives); // duplicate of the main text dropped
        Assert.Throws<TranslationException>(() => LibreTranslateClient.ParseResult("""{"error":"x"}""", "en"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5055", true)]
    [InlineData("http://localhost:5055", true)]
    [InlineData("http://[::1]:5055", true)]
    [InlineData("http://loopback:5055", true)]         // .NET rewrites "loopback" to "localhost" before connecting
    [InlineData("http://127.0.0.1.evil.com", false)]
    [InlineData("http://localhost@evil.com", false)]
    [InlineData("http://user@localhost:5055", false)]
    [InlineData("http://localhost.evil.com", false)]
    [InlineData("ftp://127.0.0.1", false)]
    public void Local_url_check(string url, bool ok) => Assert.Equal(ok, LibreTranslateClient.IsLocal(new Uri(url)));

    [Fact]
    public void Only_local_servers_are_accepted()
    {
        Assert.Throws<ArgumentException>(() => new LibreTranslateClient("https://libretranslate.com", null));
        using var ok = new LibreTranslateClient("http://127.0.0.1:5055", null);
        Assert.Null(SearchEngine.CreateTranslator(new QuicklightSettings { LibreTranslateUrl = "http://example.com" }));
    }

    static SearchEngine Engine(FakeTranslator t, QuicklightSettings? s = null) =>
        new(s ?? new QuicklightSettings(), new UsageStore(null), new AppProvider([]), translator: t);

    [Fact]
    public async Task Instant_pass_shows_pending_then_the_translation_arrives_and_is_cached()
    {
        var t = new FakeTranslator();
        var engine = Engine(t);
        var instant = await engine.SearchAsync("hello 번역", files: FileStage.None);
        Assert.Equal(ResultKind.Translation, instant[0].Kind);
        Assert.Equal(ActionType.None, instant[0].Action); // "번역 중…" does nothing on Enter
        Assert.Equal(0, t.Calls);                          // the instant pass never waits for the server

        var full = await engine.SearchAsync("hello 번역", files: FileStage.Full);
        Assert.Equal("[ko] hello", full[0].Title);
        Assert.Equal(ActionType.Copy, full[0].Action);
        Assert.Contains(full, r => r.Title == "alt: hello" && r.Kind == ResultKind.Translation);

        var again = await engine.SearchAsync("hello 번역", files: FileStage.None);
        Assert.Equal("[ko] hello", again[0].Title); // cached: instant
        Assert.Equal(1, t.Calls);
    }

    [Fact]
    public async Task Explicit_target_and_cold_server_and_off_switch()
    {
        var t = new FakeTranslator();
        Assert.Equal("[ja] 사과", (await Engine(t).SearchAsync("사과 일본어로", files: FileStage.Full))[0].Title);

        var cold = new FakeTranslator { Ready = false, CanStart = false };
        var r = await Engine(cold).SearchAsync("hello 번역", files: FileStage.Prefix);
        Assert.Equal(ActionType.None, r[0].Action);
        Assert.Contains("준비", r[0].Title);

        var off = await Engine(t, new QuicklightSettings { Translation = false }).SearchAsync("hello 번역", files: FileStage.Full);
        Assert.DoesNotContain(off, x => x.Kind == ResultKind.Translation);
    }

    [Fact]
    public async Task Mcp_translate_returns_markdown_and_structured_content()
    {
        var server = new McpServer(Engine(new FakeTranslator()), null, new McpServer.Options(AllowOpen: false));
        var r = await server.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "translate", ["arguments"] = new JsonObject { ["text"] = "hello", ["to"] = "일본어" } },
        });
        Assert.Equal("[ja] hello", r!["result"]!["structuredContent"]!["translation"]!.GetValue<string>());
        var md = r["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.StartsWith(@"# \[ja\] hello", md);
        Assert.Contains("일본어", md);
        var bad = await server.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "translate", ["arguments"] = new JsonObject { ["text"] = "hello", ["to"] = "klingon language" } },
        });
        Assert.True(bad!["result"]!["isError"]!.GetValue<bool>());
    }

    internal sealed class FakeTranslator : ITranslator
    {
        public int Calls;
        public bool Ready = true;
        public bool CanStart = true;
        public bool IsReady => Ready;
        public string? Status => Ready ? null : "번역 엔진을 준비하는 중…";

        public Task<bool> EnsureReadyAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (CanStart) Ready = true;
            return Task.FromResult(Ready);
        }

        public string? DetectAs;
        public Exception? Throw;

        public Task<TranslationResult> TranslateAsync(string text, string target, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Throw is not null) return Task.FromException<TranslationResult>(Throw);
            return Task.FromResult(new TranslationResult($"[{target}] {text}", DetectAs ?? "en", target, 90, [$"alt: {text}"]));
        }
    }
}

public class MarkdownTests
{
    static SearchResult R(string title, ResultKind kind, string? path = null, ActionType action = ActionType.Open) =>
        new() { Title = title, Kind = kind, Target = path ?? title, RevealPath = path, Subtitle = "sub", Action = action };

    [Fact]
    public void Answer_first_as_a_heading_then_groups()
    {
        var md = McpMarkdown.Search("12*7", [R("84", ResultKind.Calculator, action: ActionType.Copy), R("Calc", ResultKind.App, @"C:\calc.exe"), R("web", ResultKind.WebSearch)]);
        Assert.Contains("# 84", md);
        Assert.Contains("#### 앱", md);
        Assert.Contains(@"`C:\calc.exe`", md);
        Assert.Contains("#### 웹 검색", md);
    }

    [Fact]
    public void Non_answer_top_hit_is_listed_and_text_is_escaped()
    {
        var md = McpMarkdown.Search("x", [R("my*file_[1]", ResultKind.File, @"C:\a`b\my*file_[1]")]);
        Assert.Contains("#### 최상위 결과", md);
        Assert.Contains(@"my\*file\_\[1\]", md);
        Assert.Contains(@"`C:\a'b\my*file_[1]`", md); // backticks cannot be escaped inside code spans
    }

    [Theory]
    [InlineData("- item", @"\- item")]
    [InlineData("1. first", @"1\. first")]
    [InlineData("a & b", @"a \& b")]
    [InlineData("  - item", @"  \- item")]
    [InlineData("a	b", "a	b")]
    [InlineData("👨‍👩", "👨‍👩")]
    public void Line_start_markers_and_entities_are_escaped(string text, string expected) => Assert.Equal(expected, McpMarkdown.Escape(text));

    [Fact]
    public void Invisible_characters_are_dropped()
    {
        var md = McpMarkdown.Search("x", [R("photo\u202Egpj.exe", ResultKind.File, "C:\\a\u200B\\photo\u202Egpj.exe")]);
        Assert.DoesNotContain('\u202E', md);
        Assert.DoesNotContain('\u200B', md);
        Assert.Contains("photogpj.exe", md);
    }

    [Fact]
    public void Long_answers_become_quotes() =>
        Assert.StartsWith("> ", McpMarkdown.Answer(new string('a', 80), "ctx"));
}

public class RegionCurrencyTests
{
    [Fact]
    public void Auto_uses_the_windows_region()
    {
        var region = System.Globalization.RegionInfo.CurrentRegion.ISOCurrencySymbol;
        Assert.Equal(region, SearchEngine.ResolveDefaultCurrency("auto"));
        Assert.Equal(region, SearchEngine.ResolveDefaultCurrency(""));
        Assert.Equal(region, SearchEngine.ResolveDefaultCurrency(null));
        Assert.Equal("EUR", SearchEngine.ResolveDefaultCurrency(" eur "));
    }
}
