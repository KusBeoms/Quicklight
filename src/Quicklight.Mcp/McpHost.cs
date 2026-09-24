using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quicklight.Core;
using Quicklight.Core.Everything;

namespace Quicklight.Mcp;

/// <summary>
/// Runs the MCP stdio server: <c>Quicklight.exe --mcp</c>.
/// Flags: --read-only (no open/reveal tools), --no-history (do not use learned ranking).
/// </summary>
public static class McpHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        bool readOnly = args.Contains("--read-only");
        bool noHistory = args.Contains("--no-history");

        var settings = QuicklightSettings.Load();
        var usage = new UsageStore(noHistory ? null : UsageStore.DefaultPath);
        // An AI asked explicitly, so wait for a busy Everything instead of failing fast like the launcher does.
        var everything = settings.FileSearch ? new EverythingClient(TimeSpan.FromSeconds(15), failFast: false) : null;
        // probeRemotePaths: false, so a prompt-injected "\\\\host\\share" query cannot make us authenticate to that host.
        using var engine = new SearchEngine(settings, usage, everything: everything, fileTimeout: TimeSpan.FromSeconds(31), probeRemotePaths: false,
            rates: settings.CurrencyConversion ? SearchEngine.CreateRateStore() : null,
            translator: SearchEngine.CreateTranslator(settings));
        var server = new McpServer(engine, everything, new McpServer.Options(AllowOpen: !readOnly));

        var utf8 = new UTF8Encoding(false);
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        var writeLock = new SemaphoreSlim(1, 1);
        Log.Info("mcp server started");

        var pending = new List<Task>();
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" } });
                continue;
            }
            // Requests run concurrently so a slow file search does not block ping or other calls.
            if (node is JsonObject obj) pending.Add(Handle(obj));
            else if (node is JsonArray batch) foreach (var m in batch.OfType<JsonObject>()) pending.Add(Handle(m.DeepClone().AsObject()));
            pending.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(pending);
        return 0;

        async Task Handle(JsonObject msg)
        {
            try
            {
                var response = await server.HandleAsync(msg);
                if (response is not null) await Write(response);
            }
            catch (Exception ex)
            {
                Log.Error("mcp request failed", ex);
                if (msg["id"] is { } id)
                    await Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message } });
            }
        }

        async Task Write(JsonObject response)
        {
            await writeLock.WaitAsync();
            try { await stdout.WriteLineAsync(response.ToJsonString(McpServer.WireOptions)); }
            finally { writeLock.Release(); }
        }
    }
}
