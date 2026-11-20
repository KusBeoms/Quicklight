using System.Text.Json.Nodes;
using Quicklight.Core;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;
using Quicklight.Mcp;

namespace Quicklight.Tests;

public class McpToolSourceTests
{
    readonly List<string> _opened = [];

    McpToolSource Create()
    {
        var engine = new SearchEngine(new QuicklightSettings(), new UsageStore(null),
            new AppProvider([new AppEntry("Visual Studio Code", @"C:\x\Code.exe")]));
        return new McpToolSource(new McpServer(engine, null, new McpServer.Options(true, Opener: _opened.Add, Revealer: _opened.Add)));
    }

    [Fact]
    public void Lists_the_servers_tools_with_schema_and_read_only_flag()
    {
        var tools = Create().Tools;
        var search = Assert.Single(tools, t => t.Name == "search");
        Assert.True(search.ReadOnly);
        Assert.Equal("object", search.Schema["type"]!.GetValue<string>());
        Assert.NotNull(search.Schema["properties"]!["query"]);
        Assert.False(Assert.Single(tools, t => t.Name == "open").ReadOnly);
        Assert.False(Assert.Single(tools, t => t.Name == "reveal").ReadOnly);
    }

    [Fact]
    public async Task Calls_a_tool_and_returns_its_text()
    {
        var text = await Create().CallAsync("search", new JsonObject { ["query"] = "vsc" }, default);
        Assert.Contains("Visual Studio Code", text);
        Assert.DoesNotContain("오류", text);
    }

    [Fact]
    public async Task Tool_errors_come_back_as_text()
    {
        Assert.StartsWith("오류", await Create().CallAsync("open", new JsonObject { ["target"] = "cmd.exe /c whoami" }, default));
        Assert.StartsWith("오류", await Create().CallAsync("search", new JsonObject(), default));
        Assert.StartsWith("오류", await Create().CallAsync("nope", new JsonObject(), default)); // a JSON-RPC error
    }

    [Fact]
    public async Task Open_goes_through_the_servers_opener()
    {
        var text = await Create().CallAsync("open", new JsonObject { ["target"] = "https://example.com" }, default);
        Assert.DoesNotContain("오류", text);
        Assert.Equal(["https://example.com/"], _opened);
    }

    [Fact]
    public void Describes_the_actions_in_korean()
    {
        var source = Create();
        Assert.Equal("열기: https://example.com", source.Describe("open", new JsonObject { ["target"] = "https://example.com" }));
        Assert.Equal(@"탐색기에서 보기: C:\a", source.Describe("reveal", new JsonObject { ["path"] = @"C:\a" }));
    }
}
