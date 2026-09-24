using System.Diagnostics;
using System.Text.Json.Nodes;
using Quicklight.Core;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Quicklight.Mcp;

namespace Quicklight.Tests;

public class McpServerTests
{
    readonly List<string> _opened = [];

    McpServer Create(bool allowOpen = true)
    {
        var engine = new SearchEngine(new QuicklightSettings(), new UsageStore(null),
            new AppProvider([new AppEntry("Visual Studio Code", @"C:\x\Code.exe"), new AppEntry("메모장", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App")]));
        return new McpServer(engine, null, new McpServer.Options(allowOpen, Opener: _opened.Add, Revealer: _opened.Add));
    }

    static JsonObject Req(int id, string method, JsonObject? p = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() };

    static JsonObject Call(int id, string tool, JsonObject args) => Req(id, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });

    [Fact]
    public async Task Initialize_negotiates_protocol()
    {
        var r = await Create().HandleAsync(Req(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }));
        Assert.Equal("2025-03-26", r!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("quicklight", r["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        var unknown = await Create().HandleAsync(Req(2, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }));
        Assert.Equal(McpServer.LatestProtocol, unknown!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifications_get_no_response() =>
        Assert.Null(await Create().HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));

    [Fact]
    public async Task Unknown_method_is_an_error()
    {
        var r = await Create().HandleAsync(Req(3, "nope"));
        Assert.Equal(-32601, r!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Lists_tools_and_hides_open_in_read_only_mode()
    {
        var all = (await Create().HandleAsync(Req(1, "tools/list")))!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["search", "search_files", "convert_currency", "translate", "calculate", "open", "reveal"], all);
        var ro = (await Create(false).HandleAsync(Req(1, "tools/list")))!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("open", ro);
        var denied = await Create(false).HandleAsync(Call(2, "open", new JsonObject { ["target"] = "https://example.com" }));
        Assert.NotNull(denied!["error"]);
    }

    [Fact]
    public async Task Search_returns_ranked_structured_results()
    {
        var r = await Create().HandleAsync(Call(4, "search", new JsonObject { ["query"] = "vsc", ["limit"] = 3 }));
        var results = r!["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Equal("Visual Studio Code", results[0]!["title"]!.GetValue<string>());
        Assert.Equal("app", results[0]!["kind"]!.GetValue<string>());
        Assert.Contains("Visual Studio Code", r["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_kind_filter_and_validation()
    {
        var r = await Create().HandleAsync(Call(5, "search", new JsonObject { ["query"] = "블루투스", ["kinds"] = new JsonArray("setting") }));
        Assert.All(r!["result"]!["structuredContent"]!["results"]!.AsArray(), x => Assert.Equal("setting", x!["kind"]!.GetValue<string>()));
        var bad = await Create().HandleAsync(Call(6, "search", new JsonObject { ["query"] = "x", ["kinds"] = new JsonArray("banana") }));
        Assert.True(bad!["result"]!["isError"]!.GetValue<bool>());
        var missing = await Create().HandleAsync(Call(7, "search", new JsonObject()));
        Assert.True(missing!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Wrong_json_types_are_errors_not_crashes()
    {
        var badMethod = await Create().HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = 5 });
        Assert.Equal(-32600, badMethod!["error"]!["code"]!.GetValue<int>());
        var badName = await Create().HandleAsync(Req(2, "tools/call", new JsonObject { ["name"] = 7 }));
        Assert.Equal(-32602, badName!["error"]!["code"]!.GetValue<int>());
        var badLimit = await Create().HandleAsync(Call(3, "search", new JsonObject { ["query"] = "x", ["limit"] = "ten" }));
        Assert.True(badLimit!["result"]!["isError"]!.GetValue<bool>());
        var badQuery = await Create().HandleAsync(Call(4, "search", new JsonObject { ["query"] = 42 }));
        Assert.True(badQuery!["result"]!["isError"]!.GetValue<bool>());
        var deep = await Create().HandleAsync(Call(5, "calculate", new JsonObject { ["expression"] = new string('(', 100_000) + "1" }));
        Assert.True(deep!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Calculate()
    {
        var r = await Create().HandleAsync(Call(8, "calculate", new JsonObject { ["expression"] = "2^10" }));
        Assert.Equal(1024, r!["result"]!["structuredContent"]!["value"]!.GetValue<double>());
        var bare = await Create().HandleAsync(Call(9, "calculate", new JsonObject { ["expression"] = "42" }));
        Assert.Equal(42, bare!["result"]!["structuredContent"]!["value"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("ms-settings:display", true)]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"C:\Windows\notepad.exe", false)]            // executables are refused
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"\\attacker.example\share\doc.pdf", false)]   // UNC would leak NTLM credentials
    [InlineData("//attacker.example/share/doc.pdf", false)]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", false)] // apps go through the index check instead
    [InlineData("cmd.exe /c del *", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(@"C:\does\not\exist.txt", false)]
    [InlineData("ms-settings:display\" & calc", false)]
    [InlineData(@"relative\path.txt", false)]
    public void Open_accepts_only_safe_targets(string target, bool ok) => Assert.Equal(ok, McpServer.OpenableTarget(target) is not null);

    [Fact]
    public void Open_allows_documents_but_not_scripts()
    {
        var dir = Directory.CreateTempSubdirectory("ql-open-").FullName;
        try
        {
            var doc = Path.Combine(dir, "notes.txt");
            var script = Path.Combine(dir, "run.bat");
            var shortcut = Path.Combine(dir, "evil.lnk");
            foreach (var f in new[] { doc, script, shortcut }) File.WriteAllText(f, "x");
            Assert.Equal(doc, McpServer.OpenableTarget(doc));
            Assert.Null(McpServer.OpenableTarget(script));
            Assert.Null(McpServer.OpenableTarget(shortcut));
            Assert.Null(McpServer.OpenableTarget(script + "."));  // trailing dot trick
            Assert.Null(McpServer.OpenableTarget(doc + ":hidden.exe")); // alternate data stream
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Url_is_normalized() => Assert.Equal("https://example.com/a%20b", McpServer.OpenableTarget("https://example.com/a b"));

    [Fact]
    public async Task Open_uses_the_opener_and_rejects_bad_targets()
    {
        var server = Create();
        var ok = await server.HandleAsync(Call(10, "open", new JsonObject { ["target"] = "https://example.com" }));
        Assert.False(ok!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(["https://example.com/"], _opened);
        var bad = await server.HandleAsync(Call(11, "open", new JsonObject { ["target"] = "cmd.exe /c whoami" }));
        Assert.True(bad!["result"]!["isError"]!.GetValue<bool>());
        var exe = await server.HandleAsync(Call(12, "open", new JsonObject { ["target"] = @"C:\Windows\notepad.exe" }));
        Assert.Contains("reveal", exe!["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Single(_opened);
    }

    [Fact]
    public async Task Open_apps_only_from_the_index()
    {
        var server = Create();
        var known = await server.HandleAsync(Call(13, "open", new JsonObject { ["target"] = @"shell:AppsFolder\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App" }));
        Assert.False(known!["result"]!["isError"]!.GetValue<bool>());
        var unknown = await server.HandleAsync(Call(14, "open", new JsonObject { ["target"] = @"shell:AppsFolder\{6D809377-6AF0-444B-8957-A3773F02200E}\Evil\evil.exe" }));
        Assert.True(unknown!["result"]!["isError"]!.GetValue<bool>());
        Assert.Single(_opened);
    }

    [Fact]
    public async Task Reveal_refuses_unc()
    {
        var r = await Create().HandleAsync(Call(15, "reveal", new JsonObject { ["path"] = @"\\attacker.example\share\x" }));
        Assert.True(r!["result"]!["isError"]!.GetValue<bool>());
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task Search_does_not_probe_unc_paths_when_disabled()
    {
        var engine = new SearchEngine(new QuicklightSettings(), new UsageStore(null), new AppProvider([]), probeRemotePaths: false);
        var r = await engine.SearchAsync(@"\\attacker.example\share");
        Assert.DoesNotContain(r, x => x.Kind == Quicklight.Core.Models.ResultKind.Path);
    }

    /// <summary>The launcher exe built next to this test run (same configuration).</summary>
    static string LauncherExe()
    {
        // tests/Quicklight.Tests/bin/<Configuration>/net8.0-windows/ -> src/Quicklight/bin/<Configuration>/net8.0-windows/
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
        var configuration = tfmDir.Parent!.Name;
        var repo = tfmDir.Parent.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(repo, "src", "Quicklight", "bin", configuration, tfmDir.Name, "Quicklight.exe");
    }

    /// <summary>End to end over real stdio: the GUI exe in --mcp mode, read-only so nothing gets opened.</summary>
    [Fact]
    public async Task Stdio_round_trip()
    {
        var exe = LauncherExe();
        Assert.True(File.Exists(exe), exe);
        using var p = Process.Start(new ProcessStartInfo(exe, "--mcp --read-only --no-history")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
        })!;
        try
        {
            await p.StandardInput.WriteLineAsync(Req(1, "initialize", new JsonObject { ["protocolVersion"] = McpServer.LatestProtocol }).ToJsonString());
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await p.StandardInput.WriteLineAsync(Call(2, "calculate", new JsonObject { ["expression"] = "6*7" }).ToJsonString());
            await p.StandardInput.WriteLineAsync(Call(3, "search", new JsonObject { ["query"] = "메모장", ["kinds"] = new JsonArray("app") }).ToJsonString());
            await p.StandardInput.FlushAsync();

            var responses = new Dictionary<int, JsonNode>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (responses.Count < 3)
            {
                var line = await p.StandardOutput.ReadLineAsync(cts.Token);
                Assert.NotNull(line);
                var node = JsonNode.Parse(line!)!;
                responses[node["id"]!.GetValue<int>()] = node;
            }
            Assert.Equal("quicklight", responses[1]["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            Assert.Equal(42, responses[2]["result"]!["structuredContent"]!["value"]!.GetValue<double>());
            var apps = responses[3]["result"]!["structuredContent"]!["results"]!.AsArray();
            Assert.Contains(apps, a => a!["title"]!.GetValue<string>().Contains("메모장") || a["title"]!.GetValue<string>().Contains("Notepad"));
        }
        finally
        {
            p.StandardInput.Close();
            if (!p.WaitForExit(5000)) p.Kill();
        }
    }
}
