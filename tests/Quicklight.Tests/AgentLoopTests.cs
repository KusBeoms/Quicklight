using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Quicklight.Core.Ai;

namespace Quicklight.Tests;

public class AgentLoopTests
{
    /// <summary>Stands in for llama-server: answers each request from a script and remembers the request bodies.</summary>
    sealed class Scripted(Func<int, CancellationToken, Task<(HttpStatusCode, string)>> reply) : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];

        public Scripted(params string[] bodies) : this((i, _) => Task.FromResult((HttpStatusCode.OK, bodies[Math.Min(i, bodies.Length - 1)]))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);
            var (status, body) = await reply(Requests.Count - 1, ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    sealed class FakeTools(params AgentTool[] tools) : IAgentTools
    {
        public List<(string Name, string Args)> Calls { get; } = [];
        public IReadOnlyList<AgentTool> Tools { get; } = tools;
        public Task<string> CallAsync(string name, JsonObject args, CancellationToken ct) { Calls.Add((name, args.ToJsonString())); return Task.FromResult("결과 " + name); }
        public string Describe(string name, JsonObject args) => "설명 " + name;
    }

    sealed class FakeConsent(bool answer) : IConsent
    {
        public List<string> Asked { get; } = [];
        public Task<bool> ConfirmAsync(string summary, CancellationToken ct) { Asked.Add(summary); return Task.FromResult(answer); }
    }

    static readonly JsonObject NoArgs = new() { ["type"] = "object", ["properties"] = new JsonObject() };

    static AgentTool Read(string name = "look") => new(name, "읽기", (JsonObject)NoArgs.DeepClone(), true);
    static AgentTool Write(string name = "change") => new(name, "바꾸기", (JsonObject)NoArgs.DeepClone(), false);

    static string Call(string name, string args = "{}", string id = "c1") =>
        new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } }),
                },
            }),
        }.ToJsonString();

    static string Answer(string text) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text } }) }.ToJsonString();

    static (AiSession Session, ConversationStore Store, List<AiState> States) Create(Scripted handler, IAgentTools? tools = null, IConsent? consent = null)
    {
        var store = new ConversationStore(null);
        var session = new AiSession(store, tools is null ? [] : [tools], consent, handler);
        var states = new List<AiState>();
        session.StateChanged += states.Add;
        return (session, store, states);
    }

    /// <summary>The "tool" messages of a request, as text.</summary>
    static string[] ToolMessages(JsonObject request) =>
        request["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Answers_without_tools()
    {
        var handler = new Scripted(Answer("<think>음</think>안녕하세요"));
        var (session, store, states) = Create(handler);
        var conv = store.Create();

        var reply = await session.AskAsync(conv, "안녕", null, default);

        Assert.Equal("안녕하세요", reply.Text);
        Assert.Equal(ChatRole.Assistant, reply.Role);
        Assert.False(reply.IsError);
        Assert.Equal([ChatRole.User, ChatRole.Assistant], conv.Messages.Select(m => m.Role));
        Assert.Equal([AiState.Working, AiState.Answer], states);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request["tools"]); // no tools: no tools array
        Assert.False(request["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("system", request["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_read_only_tool_runs_without_consent_and_its_result_goes_back_as_data()
    {
        var tools = new FakeTools(Read());
        var consent = new FakeConsent(false);
        var handler = new Scripted(Call("look", """{"q":"x"}"""), Answer("끝"));
        var (session, store, states) = Create(handler, tools, consent);

        var reply = await session.AskAsync(store.Create(), "봐 줘", null, default);

        Assert.Equal("끝", reply.Text);
        Assert.Equal([("look", """{"q":"x"}""")], tools.Calls);
        Assert.Empty(consent.Asked);
        Assert.Equal([AiState.Working, AiState.Searching, AiState.Working, AiState.Answer], states);
        Assert.NotNull(handler.Requests[0]["tools"]);
        Assert.Equal("look", handler.Requests[0]["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        var back = Assert.Single(ToolMessages(handler.Requests[1]));
        Assert.StartsWith("<data>", back);
        Assert.EndsWith("</data>", back);
        Assert.Contains("결과 look", back);
        // The assistant's tool call is part of the second request, and the tool message answers its id.
        var messages = handler.Requests[1]["messages"]!.AsArray();
        Assert.Equal("c1", messages[^1]!["tool_call_id"]!.GetValue<string>());
        Assert.NotNull(messages[^2]!["tool_calls"]);
    }

    [Fact]
    public async Task A_tool_that_changes_things_asks_first()
    {
        var tools = new FakeTools(Write());
        var consent = new FakeConsent(true);
        var (session, store, states) = Create(new Scripted(Call("change"), Answer("했습니다")), tools, consent);

        await session.AskAsync(store.Create(), "바꿔", null, default);

        Assert.Equal(["설명 change"], consent.Asked);
        Assert.Single(tools.Calls);
        Assert.DoesNotContain(AiState.Searching, states); // only read-only tools show Searching
    }

    [Fact]
    public async Task A_declined_tool_is_not_run_and_the_model_goes_on()
    {
        var tools = new FakeTools(Write());
        var handler = new Scripted(Call("change"), Answer("알겠습니다"));
        var (session, store, _) = Create(handler, tools, new FakeConsent(false));

        var reply = await session.AskAsync(store.Create(), "바꿔", null, default);

        Assert.Empty(tools.Calls);
        Assert.Contains("사용자가 거절했습니다", Assert.Single(ToolMessages(handler.Requests[1])));
        Assert.Equal("알겠습니다", reply.Text);
    }

    [Fact]
    public async Task Without_a_consent_handler_a_changing_tool_is_declined()
    {
        var tools = new FakeTools(Write());
        var handler = new Scripted(Call("change"), Answer("ok"));
        var (session, store, _) = Create(handler, tools, consent: null);

        await session.AskAsync(store.Create(), "바꿔", null, default);

        Assert.Empty(tools.Calls);
        Assert.Contains("사용자가 거절했습니다", Assert.Single(ToolMessages(handler.Requests[1])));
    }

    [Fact]
    public async Task An_unknown_tool_and_bad_arguments_become_results_not_crashes()
    {
        var tools = new FakeTools(Read());
        var handler = new Scripted(Call("nope"), Call("look", "{not json"), Answer("ok"));
        var (session, store, _) = Create(handler, tools);

        var reply = await session.AskAsync(store.Create(), "x", null, default);

        Assert.False(reply.IsError);
        Assert.Empty(tools.Calls);
        Assert.Contains("알 수 없는 도구", Assert.Single(ToolMessages(handler.Requests[1])));
        Assert.Equal(2, ToolMessages(handler.Requests[2]).Length);
    }

    [Fact]
    public async Task Long_tool_results_are_cut()
    {
        var tools = new LongTools();
        var handler = new Scripted(Call("look"), Answer("ok"));
        var (session, store, _) = Create(handler, tools);

        await session.AskAsync(store.Create(), "x", null, default);

        var back = Assert.Single(ToolMessages(handler.Requests[1]));
        Assert.True(back.Length < 5200);
        Assert.EndsWith("[잘림]\n</data>", back);
    }

    sealed class LongTools : IAgentTools
    {
        public IReadOnlyList<AgentTool> Tools { get; } = [Read()];
        public Task<string> CallAsync(string name, JsonObject args, CancellationToken ct) => Task.FromResult(new string('가', 20000));
        public string Describe(string name, JsonObject args) => name;
    }

    [Fact]
    public async Task Stops_after_twelve_steps()
    {
        var tools = new FakeTools(Read());
        var handler = new Scripted(Call("look")); // asks for the tool forever
        var (session, store, states) = Create(handler, tools);
        var conv = store.Create();

        var reply = await session.AskAsync(conv, "x", null, default);

        Assert.True(reply.IsError);
        Assert.Equal(12, handler.Requests.Count);
        Assert.Equal(12, tools.Calls.Count);
        Assert.Equal(AiState.Error, states[^1]);
        Assert.Same(reply, conv.Messages[^1]);
    }

    [Fact]
    public async Task A_server_error_is_an_error_message_not_an_exception()
    {
        var handler = new Scripted((_, _) => Task.FromResult((HttpStatusCode.InternalServerError, "{}")));
        var (session, store, states) = Create(handler);
        var conv = store.Create();

        var reply = await session.AskAsync(conv, "x", null, default);

        Assert.True(reply.IsError);
        Assert.Contains("500", reply.Text);
        Assert.Equal([AiState.Working, AiState.Error], states);
        Assert.Equal(2, conv.Messages.Count);
    }

    [Fact]
    public async Task Cancelling_throws_and_ends_idle()
    {
        var started = new TaskCompletionSource();
        var handler = new Scripted(async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return (HttpStatusCode.OK, ""); });
        var (session, store, states) = Create(handler);
        using var cts = new CancellationTokenSource();

        var ask = session.AskAsync(store.Create(), "x", null, cts.Token);
        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);
        Assert.Equal([AiState.Working, AiState.Idle], states);
    }

    [Fact]
    public async Task Earlier_messages_are_sent_and_errors_are_not()
    {
        var handler = new Scripted(Answer("둘"));
        var (session, store, _) = Create(handler);
        var conv = store.Create();
        store.Add(conv, new ChatMessage { Role = ChatRole.User, Text = "하나" });
        store.Add(conv, new ChatMessage { Role = ChatRole.Assistant, Text = "실패", IsError = true });
        store.Add(conv, new ChatMessage { Role = ChatRole.Assistant, Text = "답 하나" });

        await session.AskAsync(conv, "둘?", null, default);

        var sent = handler.Requests[0]["messages"]!.AsArray().Select(m => m!["content"]!.GetValue<string>()).ToArray();
        Assert.Equal(["하나", "답 하나", "둘?"], sent.Skip(1));
    }

    [Fact]
    public async Task Images_are_sent_as_pictures_only_when_the_server_can_see()
    {
        var png = Path.Combine(Path.GetTempPath(), $"ql-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(png, [1, 2, 3, 4]);
        try
        {
            var seeing = new Scripted(Answer("ok"));
            var (withVision, store, _) = Create(seeing);
            withVision.Vision = true;
            await withVision.AskAsync(store.Create(), "이게 뭐야", [new Attachment("image", png)], default);
            var parts = seeing.Requests[0]["messages"]![1]!["content"]!.AsArray();
            Assert.Equal("text", parts[0]!["type"]!.GetValue<string>());
            Assert.StartsWith("data:image/png;base64,AQIDBA==", parts[1]!["image_url"]!["url"]!.GetValue<string>());

            var blind = new Scripted(Answer("ok"));
            var (withoutVision, store2, _) = Create(blind);
            withoutVision.Vision = false;
            await withoutVision.AskAsync(store2.Create(), "이게 뭐야", [new Attachment("image", png)], default);
            Assert.Contains(png, blind.Requests[0]["messages"]![1]!["content"]!.GetValue<string>());
        }
        finally { File.Delete(png); }
    }

    [Fact]
    public async Task Small_text_files_are_included_and_binary_ones_only_named()
    {
        var dir = Directory.CreateTempSubdirectory("ql-att-").FullName;
        try
        {
            var txt = Path.Combine(dir, "a.txt");
            var bin = Path.Combine(dir, "b.bin");
            File.WriteAllText(txt, "파일 내용");
            File.WriteAllBytes(bin, [0xFF, 0xFE, 0x00, 0x80]);
            var handler = new Scripted(Answer("ok"));
            var (session, store, _) = Create(handler);

            await session.AskAsync(store.Create(), "요약", [new Attachment("file", txt), new Attachment("file", bin)], default);

            var content = handler.Requests[0]["messages"]![1]!["content"]!.GetValue<string>();
            Assert.Contains("파일 내용", content);
            Assert.Contains(bin, content);
        }
        finally { Directory.Delete(dir, true); }
    }
}
