using System.Text.Json.Nodes;
using Quicklight.Core.Ai;

namespace Quicklight.Mcp;

/// <summary>The MCP server's tools (search, open, ...) for the local AI, called in-process through JSON-RPC.</summary>
public sealed class McpToolSource(McpServer server) : IAgentTools
{
    readonly Lazy<IReadOnlyList<AgentTool>> _tools = new(() => List(server));

    public IReadOnlyList<AgentTool> Tools => _tools.Value;

    static IReadOnlyList<AgentTool> List(McpServer server)
    {
        // tools/list never waits on anything (only search does), so this finishes at once.
        var response = server.HandleAsync(Request("tools/list", new JsonObject())).GetAwaiter().GetResult();
        return (response?["result"]?["tools"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(t => new AgentTool(
                t["name"]!.GetValue<string>(),
                t["description"]?.GetValue<string>() ?? "",
                t["inputSchema"] as JsonObject is { } schema ? (JsonObject)schema.DeepClone() : new JsonObject { ["type"] = "object" },
                t["annotations"]?["readOnlyHint"] is JsonValue v && v.TryGetValue<bool>(out var ro) && ro))
            .ToList();
    }

    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        var response = await server.HandleAsync(Request("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }), ct);
        if (response?["error"] is { } error) return "오류: " + error["message"]?.GetValue<string>();
        var result = response?["result"];
        var text = string.Join("\n", (result?["content"] as JsonArray ?? []).Select(c => c?["text"]?.GetValue<string>()).Where(t => t is not null));
        return result?["isError"]?.GetValue<bool>() == true ? "오류: " + text : text;
    }

    public string Describe(string name, JsonObject args) => name switch
    {
        "open" => $"열기: {Arg(args, "target")}",
        "reveal" => $"탐색기에서 보기: {Arg(args, "path")}",
        _ => name,
    };

    static string Arg(JsonObject args, string key) => args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static JsonObject Request(string method, JsonObject p) => new() { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = p };
}
