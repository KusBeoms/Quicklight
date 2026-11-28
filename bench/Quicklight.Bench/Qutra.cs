using System.Diagnostics;
using System.IO.Compression;
using Quicklight.Core;
using Quicklight.Core.Ai;
using Quicklight.Mcp;

namespace Quicklight.Bench;

/// <summary>
/// Qutra's real tool list (the MCP tools, launcher tools, file organizer, screen tools) behind a recorder: every call
/// is written down, and only pure computation (calculate, units, dates, currency) actually runs. Search, recent files
/// and the window list answer from <see cref="FakePc"/>. Timers, volume, Bluetooth, file moves and opening things never happen.
/// </summary>
sealed class Recorder(McpToolSource mcp, IConsent consent) : IAgentTools
{
    static readonly JsonObject Empty = new() { ["type"] = "object", ["properties"] = new JsonObject() };
    // The launcher's screen tools live in the WPF project; their definitions, copied.
    static readonly AgentTool[] Screen =
    [
        new("get_context", "The user's context from the moment they opened Quicklight: the window that was in front (title and program), the files selected in File Explorer, and the text selected in that app. Use it when the user says 'this', 'this file', 'the selected text' or similar.", Empty, true),
        new("look_at_screen", "Captures the window the user was working in (or the whole screen) and returns the text recognized in it; when the AI model can see pictures, the picture is attached too. Use it when the user asks about what is on the screen, in this window, or about an error or page they are looking at.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["target"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("window", "screen") } } }, true),
    ];

    readonly LauncherTools _launcher = new();
    readonly FileOrganizerTools _organizer = new(consent);
    public List<Call> Calls { get; } = [];
    public Case? Current { get; set; }

    public IReadOnlyList<AgentTool> Tools => [.. mcp.Tools, .. _launcher.Tools, .. _organizer.Tools, .. Screen];

    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        Calls.Add(new Call(name, (JsonObject)args.DeepClone()));
        if (Current?.Obj("toolResults")?[name]?.GetValue<string>() is { } canned) return Text.Expand(canned);
        return name switch
        {
            "get_context" => Current?.Str("context") ?? "앞 창: 메모장 - 제목 없음 (notepad.exe)\n탐색기 선택: 없음\n선택한 글: 없음",
            "look_at_screen" => Current?.Str("screen") ?? "화면에서 읽은 글자: (없음)",
            "list_timers" => "예약된 타이머/알림 없음",
            "list_folder" => "폴더 목록 (이름, 크기, 수정 시각)\n- Program Files  폴더\n- Users  폴더\n- Windows  폴더\n- 보고서.docx  20KB  2026-11-01",
            // Tools that read this PC answer from a made-up PC: the results are the same everywhere, and none of the
            // user's file names, windows or recent files end up in the (public) results.
            "search" or "search_files" => FakePc.Search(args["query"]?.ToString() ?? ""),
            "recent_files" => FakePc.Recent(),
            "list_windows" => FakePc.Windows,
            // Pure computation runs for real.
            "calculate" or "convert_unit" or "date_calc" or "convert_currency" => await mcp.CallAsync(name, args, ct),
            _ => "완료했습니다.",
        };
    }

    public string Describe(string name, JsonObject args) =>
        mcp.Tools.Any(t => t.Name == name) ? mcp.Describe(name, args) : _launcher.Tools.Any(t => t.Name == name) ? _launcher.Describe(name, args) : name;
}

/// <summary>The made-up PC behind search, recent files and the window list in the Qutra cases.</summary>
static class FakePc
{
    static readonly (string Kind, string Name, string Path, int DaysAgo)[] Items =
    [
        ("앱", "메모장", @"C:\Windows\System32\notepad.exe", 0), ("앱", "계산기", "calculator:", 0), ("앱", "그림판", @"C:\Windows\System32\mspaint.exe", 0),
        ("앱", "카카오톡", @"C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe", 0), ("앱", "Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", 0),
        ("파일", "견적서_한빛상사_2026년11월.pdf", @"C:\Users\me\Documents\업무\견적서_한빛상사_2026년11월.pdf", 5),
        ("파일", "분기보고서_3분기_최종.xlsx", @"C:\Users\me\Documents\업무\분기보고서_3분기_최종.xlsx", 20),
        ("파일", "회의록_2026-11-20_기획회의.docx", @"C:\Users\me\Documents\업무\회의록_2026-11-20_기획회의.docx", 8),
        ("파일", "가계부_2026.xlsx", @"C:\Users\me\Documents\가계부_2026.xlsx", 1),
        ("파일", "제주도여행_성산일출봉.jpg", @"C:\Users\me\Pictures\제주도여행_성산일출봉.jpg", 90),
        ("파일", "임대차계약서_서명본.pdf", @"C:\Users\me\Downloads\임대차계약서_서명본.pdf", 12),
        ("폴더", "Downloads", @"C:\Users\me\Downloads", 0), ("폴더", "Documents", @"C:\Users\me\Documents", 0),
    ];

    public const string Windows = "열린 창:\n- 메모장 - 제목 없음 (notepad.exe)\n- 뉴스 - Microsoft Edge (msedge.exe)\n- 문서 - 파일 탐색기 (explorer.exe)";

    public static string Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Contains(':')).Select(Text.Norm).ToList();
        var hits = Items.Where(i => words.Count > 0 && words.Any(w => Text.Norm(i.Name).Contains(w) || Text.Norm(i.Path).Contains(w))).ToList();
        return hits.Count == 0 ? $"'{query}' 검색 결과 없음" : string.Join("\n", hits.Select(h => $"- [{h.Kind}] {h.Name}  {h.Path}"));
    }

    public static string Recent() =>
        "최근 수정한 파일:\n" + string.Join("\n", Items.Where(i => i.Kind == "파일").OrderBy(i => i.DaysAgo).Select(i => $"- {i.Name}  {i.Path}  ({i.DaysAgo}일 전)"));
}

sealed class Consent : IConsent
{
    public bool Allow { get; set; } = true;
    public List<string> Asked { get; } = [];
    public Task<bool> ConfirmAsync(string summary, CancellationToken ct) { Asked.Add(summary); return Task.FromResult(Allow); }
}

/// <summary>Runs the Qutra cases on the real model, each <c>reps</c> times.</summary>
static class Qutra
{
    public static async Task<(List<ItemResult> Items, double WarmUpMs)> RunAsync(InProcessSearch search, IReadOnlyList<Case> cases, int reps, Action<string> log)
    {
        var consent = new Consent();
        var recorder = new Recorder(new McpToolSource(new McpServer(search.Engine, search.Everything, new McpServer.Options(AllowOpen: true))), consent);
        var store = new ConversationStore(null);
        using var session = new AiSession(store, [recorder], consent);
        var work = Path.Combine(Path.GetTempPath(), "ql-bench-qutra");
        Directory.CreateDirectory(work);
        try
        {
            var sw = Stopwatch.StartNew();
            await session.WarmUpAsync();
            double warm = sw.Elapsed.TotalMilliseconds;
            log($"  모델 준비 {warm / 1000:F1}초");

            var results = new List<ItemResult>();
            int done = 0;
            foreach (var c in cases)
            {
                var r = new ItemResult { Id = c.Id, Cat = c.Cat, Tier = c.Tier, Holdout = c.Holdout };
                var times = new List<double>();
                var speeds = new List<double>();
                bool skipped = false;
                for (int i = 0; i < reps && !skipped; i++)
                {
                    recorder.Calls.Clear(); consent.Asked.Clear();
                    recorder.Current = c;
                    consent.Allow = c.Str("consent") != "deny";
                    var t = Stopwatch.StartNew();
                    string? answer;
                    try { answer = await AnswerAsync(session, store, c, work); }
                    catch (Exception ex) { answer = "예외: " + ex.Message; }
                    if (answer is null) { skipped = true; break; } // e.g. PDF reading not installed
                    times.Add(t.Elapsed.TotalMilliseconds);
                    if (session.LastTimings?["predicted_per_second"]?.GetValue<double>() is { } tps) speeds.Add(tps);
                    var why = Checks.Fail(c.Obj("checks"), answer, recorder.Calls, consent.Asked);
                    r.Passes.Add(why is null);
                    if (why is not null) r.Why = why;
                    r.Answer = Text.Flat(answer) + (recorder.Calls.Count > 0 ? "  [도구: " + string.Join(", ", recorder.Calls.Select(x => x.Name + x.Args.ToJsonString())) + "]" : "");
                }
                if (skipped) { log($"  - {c.Id} 건너뜀 (필요한 기능이 설치되지 않음)"); continue; }
                r.Ms = Launcher.Median(times);
                if (speeds.Count > 0) r.TokensPerSecond = Launcher.Median(speeds);
                results.Add(r);
                done++;
                log($"  {(r.Pass >= 1 ? "✓" : r.Pass > 0 ? "△" : "✗")} [{done}/{cases.Count}] {c.Id} {r.Pass * 100:F0}%" + (r.Pass < 1 ? $" — {r.Why}" : ""));
            }
            return (results, warm);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    /// <summary>The text the user would see for this case. Null when the case cannot run on this PC.</summary>
    static async Task<string?> AnswerAsync(AiSession session, ConversationStore store, Case c, string work)
    {
        string data = Text.Expand(c.Str("data") ?? "");
        string q = Text.Expand(c.Str("q") ?? "");
        switch (c.Str("task"))
        {
            case "summary": return await session.CompleteAsync(Prompts.Summary, data, default) ?? "";
            case "clip-summary": return await session.CompleteAsync(Prompts.ClipSummary, data, default) ?? "";
            case "polish": return await session.CompleteAsync(Prompts.Polish, data, default) ?? "";
            case "csv": return await session.CompleteAsync(Prompts.Csv, data, default) ?? "";
            case "json": return ClipboardActions.Local("json", data) ?? await session.CompleteAsync(Prompts.Json, data, default) ?? "";
            case "translate": return await session.CompleteAsync(Prompts.Translate(c.Str("lang") ?? "English"), data, default) ?? "";
            case "term-cmd": return TerminalAi.CleanCommand(await session.CompleteAsync(Prompts.TerminalCommand, TerminalAi.CommandUser(@"C:\work", q), default) ?? "");
            case "term-explain":
                return TerminalAi.ScrubExplain(await session.CompleteAsync(Prompts.TerminalExplain, TerminalAi.ExplainUser(c.Str("cmd") ?? "", data), default) ?? "");
            case "file-query": return FileQueryDetector.CleanQuery(await session.CompleteAsync(Prompts.FileQuery(DateTime.Today), q, default)) ?? "";
            case "doc-summary":
            {
                var path = MakeDocument(work, c.Str("doc") ?? "txt", data);
                if (!DocumentText.Supports(path)) return null;
                var body = DocumentText.Extract(path);
                return body is null ? "(문서에서 글을 뽑지 못함)" : await session.CompleteAsync(Prompts.Summary, body, default) ?? "";
            }
            case "chat":
            {
                var conv = store.Create();
                List<Attachment>? attach = null;
                if (c.Str("attach") is { } text)
                {
                    var file = Path.Combine(work, c.Str("attachName") ?? "첨부.txt");
                    File.WriteAllText(file, Text.Expand(text));
                    attach = [new Attachment("file", file)];
                }
                string reply = "";
                foreach (var turn in c.Raw["turns"] is JsonArray turns ? turns.Select(t => Text.Expand(t!.GetValue<string>())) : [q])
                {
                    reply = (await session.AskAsync(conv, turn, attach, default)).Text;
                    attach = null;
                }
                return reply;
            }
            default: throw new InvalidDataException($"{c.Id}: 모르는 task '{c.Str("task")}'");
        }
    }

    /// <summary>A small document of the given type holding <paramref name="text"/>, in the shapes DocumentText reads.</summary>
    static string MakeDocument(string dir, string type, string text)
    {
        var path = Path.Combine(dir, "doc." + type);
        if (File.Exists(path)) File.Delete(path);
        var paras = text.Split('\n').Select(l => System.Security.SecurityElement.Escape(l));
        if (type is "txt" or "md") { File.WriteAllText(path, text); return path; }
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string xml) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(xml); }
        switch (type)
        {
            case "docx": Entry("word/document.xml", "<w:document><w:body>" + string.Concat(paras.Select(p => $"<w:p><w:r><w:t>{p}</w:t></w:r></w:p>")) + "</w:body></w:document>"); break;
            case "pptx":
                int n = 0;
                foreach (var p in paras) Entry($"ppt/slides/slide{++n}.xml", $"<p:sld><a:p><a:r><a:t>{p}</a:t></a:r></a:p></p:sld>");
                break;
            case "hwpx": Entry("Contents/section0.xml", "<hs:sec>" + string.Concat(paras.Select(p => $"<hp:p><hp:t>{p}</hp:t></hp:p>")) + "</hs:sec>"); break;
            default: throw new InvalidDataException("모르는 문서 종류 " + type);
        }
        return path;
    }
}
