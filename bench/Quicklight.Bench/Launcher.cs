using System.Diagnostics;
using System.Text.RegularExpressions;
using Quicklight.Core;
using Quicklight.Core.Ai;
using Quicklight.Core.Currency;
using Quicklight.Core.Everything;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;

namespace Quicklight.Bench;

sealed record Hit(string Kind, string Title, string Subtitle, string Target, string? Action);

/// <summary>Where the launcher's answers come from: this source in-process, or any Quicklight.exe as an MCP server.</summary>
interface ISearch : IDisposable
{
    string Mode { get; }
    bool InProcess { get; }
    Task<List<Hit>> SearchAsync(string query, bool instant);
}

/// <summary>
/// The launcher's own engine and providers (the AI row included), with copies of the app index and the rate cache in a
/// temp folder and an in-memory history: nothing of the user's is written.
/// </summary>
sealed class InProcessSearch : ISearch
{
    public string Mode => "소스 (프로세스 안)";
    public bool InProcess => true;
    public SearchEngine Engine { get; }
    public EverythingClient Everything { get; } = new();
    readonly string _state = Path.Combine(Path.GetTempPath(), "ql-bench-state");

    public InProcessSearch()
    {
        Directory.CreateDirectory(_state);
        string apps = Copy(AppDatabase.DefaultPath), rates = Copy(ExchangeRateStore.DefaultCachePath);
        var settings = new QuicklightSettings { AiEnabled = true };
        // ponytail: rates never refresh during a run (10-year max age); currency checks look at the format, not the rate.
        Engine = new SearchEngine(settings, new UsageStore(null), new AppProvider(apps), Everything,
            rates: new ExchangeRateStore(new HttpRateSource(), rates, maxAge: TimeSpan.FromDays(3650)), includeWindows: true);
    }

    string Copy(string path)
    {
        var to = Path.Combine(_state, Path.GetFileName(path));
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Copy(path + suffix, to + suffix, overwrite: true);
        return to;
    }

    public async Task<List<Hit>> SearchAsync(string query, bool instant) =>
        (await Engine.SearchAsync(query, 10, files: instant ? FileStage.None : FileStage.Full))
            .Select(r => new Hit(Launcher.Snake(r.Kind.ToString()), r.Title, r.Subtitle ?? "", r.Target ?? "", r.Action.ToString())).ToList();

    public void Dispose()
    {
        Engine.Dispose();
        try { Directory.Delete(_state, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Any Quicklight.exe (an old release too) in "--mcp --read-only --no-history": it can neither open nor learn anything.</summary>
sealed class McpSearch : ISearch
{
    readonly Process _p;
    int _id;
    public string Mode { get; }
    public bool InProcess => false;

    public McpSearch(string exe)
    {
        // A release exe comes with Quicklight.exe.sig: it must carry the release key's signature (as the updater checks).
        var sig = exe + ".sig";
        string signed = "서명 없음";
        if (File.Exists(sig))
        {
            if (!Quicklight.Core.Update.UpdateSignature.Verify(File.ReadAllBytes(exe), File.ReadAllText(sig)))
                throw new InvalidOperationException("서명이 맞지 않는 exe 라 실행하지 않습니다: " + exe);
            signed = "서명 확인";
        }
        Mode = $"exe (MCP, {signed})";
        _p = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--mcp", "--read-only", "--no-history" }, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 })
            ?? throw new InvalidOperationException("exe를 실행하지 못했습니다");
        _p.ErrorDataReceived += (_, _) => { };
        _p.BeginErrorReadLine();
        Rpc("initialize", new JsonObject { ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "bench", ["version"] = "1" } });
    }

    JsonNode? Rpc(string method, JsonObject args)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = args };
        _p.StandardInput.WriteLine(msg.ToJsonString());
        _p.StandardInput.Flush();
        while (_p.StandardOutput.ReadLine() is { } line)
            if (JsonNode.Parse(line) is { } r && r["id"]?.GetValue<int>() == _id) return r["result"];
        throw new IOException("MCP 서버가 끝났습니다");
    }

    public Task<List<Hit>> SearchAsync(string query, bool instant)
    {
        var r = Rpc("tools/call", new JsonObject { ["name"] = "search", ["arguments"] = new JsonObject { ["query"] = query, ["limit"] = 10 } });
        var list = (r?["structuredContent"]?["results"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => new Hit(o["kind"]?.GetValue<string>() ?? "", o["title"]?.GetValue<string>() ?? "", o["subtitle"]?.GetValue<string>() ?? "", o["target"]?.GetValue<string>() ?? "", null)).ToList();
        return Task.FromResult(list);
    }

    public void Dispose()
    {
        try { _p.StandardInput.Close(); if (!_p.WaitForExit(5000)) _p.Kill(); } catch (InvalidOperationException) { }
        _p.Dispose();
    }
}

/// <summary>Runs the launcher cases: ranking, routing, robustness, and the time to each answer.</summary>
static class Launcher
{
    public static string Snake(string pascal) => Regex.Replace(pascal, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    // Answers that need the launcher's own providers (AI row, timers) or its detectors: not over MCP.
    static readonly HashSet<string> InProcessOnly = ["routing", "timer"];

    public static async Task<List<ItemResult>> RunAsync(ISearch search, IReadOnlyList<Case> cases, string fixtures, Action<string> log)
    {
        var results = new List<ItemResult>();
        using var files = new Fixtures(fixtures);
        bool indexed = await files.WaitIndexedAsync(search, log);
        foreach (var c in cases)
        {
            if (!search.InProcess && InProcessOnly.Contains(c.Cat)) continue;
            if (c.Cat == "file" && !indexed) continue;
            var r = new ItemResult { Id = c.Id, Cat = c.Cat, Tier = c.Tier, Holdout = c.Holdout };
            try { await RunOne(search, c, r); }
            catch (Exception ex) { r.Passes.Add(false); r.Why = "예외: " + ex.Message; }
            results.Add(r);
            if (r.Pass < 1) log($"  ✗ {c.Id} {c.Str("q")} — {r.Why}");
        }
        return results;
    }

    static async Task RunOne(ISearch search, Case c, ItemResult r)
    {
        var q = Text.Expand(c.Str("q") ?? "");
        var expect = c.Obj("expect") ?? new JsonObject();

        if (c.Cat == "routing")
        {
            var routes = Routes(q);
            var want = c.Str("route")!;
            r.Answer = routes.Count == 0 ? "none" : string.Join(",", routes);
            bool ok = want == "none" ? routes.Count == 0 : routes.Contains(want);
            r.Passes.Add(ok);
            if (!ok) r.Why = $"기대 {want}, 실제 {r.Answer}";
            return;
        }

        // One run each: with hundreds of items the p50/p95 come from the spread across items, and the whole launcher part
        // stays near a minute (a full search with files is ~250 ms).
        var sw = Stopwatch.StartNew();
        var hits = await search.SearchAsync(q, instant: false);
        r.Ms = sw.Elapsed.TotalMilliseconds;
        if (search.InProcess)
        {
            sw.Restart();
            await search.SearchAsync(q, instant: true);
            r.InstantMs = sw.Elapsed.TotalMilliseconds;
        }
        else r.InstantMs = r.Ms;
        r.Answer = string.Join(" | ", hits.Take(4).Select(h => $"{h.Kind}:{Public(h)}"));

        if (c.Cat == "robust")
        {
            double max = c.Raw["maxMs"]?.GetValue<double>() ?? 3000;
            bool ok = r.Ms <= max;
            r.Passes.Add(ok);
            if (!ok) r.Why = $"{r.Ms:F0}ms (최대 {max})";
            return;
        }

        int rank = expect["rank"]?.GetValue<int>() ?? 1;
        bool found = hits.Take(rank).Any(h => Matches(h, expect));
        r.Passes.Add(found);
        if (!found) r.Why = $"{rank}위 안에 없음: {r.Answer}";
    }

    /// <summary>What the saved (public) result may show of a hit: the user's own files and folders are not named.</summary>
    static string Public(Hit h) =>
        h.Kind is "file" or "folder" && !h.Target.Contains(Path.Combine(Path.GetTempPath(), "ql-bench"), StringComparison.OrdinalIgnoreCase) ? "(이 PC의 파일)" : h.Title;

    static bool Matches(Hit h, JsonObject e)
    {
        if (e["kind"] is { } k && !(k is JsonArray a ? a.Any(x => x!.GetValue<string>() == h.Kind) : k.GetValue<string>() == h.Kind)) return false;
        if (e["action"]?.GetValue<string>() is { } act && act != h.Action) return false;
        if (e["titleHas"]?.GetValue<string>() is { } t && !Text.Norm(h.Title).Contains(Text.Norm(Text.Expand(t)))) return false;
        if (e["titleAny"] is JsonArray any && !any.Any(x => Text.Norm(h.Title).Contains(Text.Norm(Text.Expand(x!.GetValue<string>()))))) return false;
        if (e["subtitleHas"]?.GetValue<string>() is { } s && !Text.Norm(h.Subtitle).Contains(Text.Norm(Text.Expand(s)))) return false;
        if (e["targetHas"]?.GetValue<string>() is { } g && !Text.Norm(h.Target).Contains(Text.Norm(Text.Expand(g)))) return false;
        return true;
    }

    /// <summary>Where the launcher sends this input besides search: translation card, AI file search, the "Qutra에게 묻기" row.</summary>
    public static List<string> Routes(string q)
    {
        var routes = new List<string>();
        if (TranslateParser.Parse(q) is not null) routes.Add("translate");
        if (FileQueryDetector.IsFileQuery(q)) routes.Add("file");
        if (QuestionDetector.IsQuestion(q)) routes.Add("ai");
        return routes;
    }

    public static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}

/// <summary>
/// bench\fixtures\files.json made real under %TEMP%\ql-bench: the target files the file cases look for among
/// thousands of look-alike decoys. Deleted at the end.
/// </summary>
sealed class Fixtures : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ql-bench");
    readonly string _sentinel;

    public Fixtures(string spec)
    {
        var o = JsonNode.Parse(File.ReadAllText(spec))!.AsObject();
        _sentinel = o["sentinel"]!.GetValue<string>();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        Directory.CreateDirectory(Root);
        var rng = new Random(7);
        foreach (var d in o["decoys"]!.AsArray().OfType<JsonObject>())
            for (int i = 1; i <= d["count"]!.GetValue<int>(); i++)
                Make(d["pattern"]!.GetValue<string>().Replace("{n}", i.ToString()), rng.Next(1, 400));
        foreach (var t in o["targets"]!.AsArray().OfType<JsonObject>())
            Make(t["path"]!.GetValue<string>(), t["daysAgo"]?.GetValue<int>() ?? 2);
        Make(_sentinel, 0);
    }

    void Make(string relative, int daysAgo)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "ql-bench");
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-daysAgo));
    }

    /// <summary>Waits (up to a minute) until Everything has indexed the new files.</summary>
    public async Task<bool> WaitIndexedAsync(ISearch search, Action<string> log)
    {
        var name = Path.GetFileNameWithoutExtension(_sentinel);
        for (int i = 0; i < 120; i++)
        {
            if ((await search.SearchAsync(name, instant: false)).Any(h => h.Title.Contains(name, StringComparison.OrdinalIgnoreCase))) return true;
            await Task.Delay(500);
        }
        log("  ! Everything 이 시험 파일을 색인하지 않아 파일 찾기는 건너뜁니다");
        return false;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
