using System.Diagnostics;
using Quicklight.Bench;
using Quicklight.Core;
using Quicklight.Core.Ai;

// Quicklight evaluation. Usage (from the repository root):
//   dotnet run --project bench\Quicklight.Bench -c Release -- run              launcher + Qutra, saved as bench\results\v<version>.*
//   ... -- run --launcher | --qutra                                             one part only
//   ... -- run --quick                                                          1 repetition, a sample of the Qutra cases (results\quick, no history)
//   ... -- run --exe <Quicklight.exe>                                           launcher part against any build (an old release too) over MCP
//   ... -- run --only calc-                                                     cases whose id starts with this
//   ... -- image bench\results\v1.2.0.json                                      redraw the picture
Console.OutputEncoding = System.Text.Encoding.UTF8;
var root = FindRoot();
var casesDir = Path.Combine(root, "bench", "cases");
var baseResults = Path.Combine(root, "bench", "results");
// Released exes (--exe) are scored over MCP, without the routing and timer items: they get their own folder and
// history, so they never overwrite or mix with the in-process result of the same version.
var releasesDir = Path.Combine(baseResults, "releases");
var resultsDir = Opt("--exe") is null ? baseResults : releasesDir;
var suite = Suite.Load(Path.Combine(casesDir, "suite.json"));

if (args.Length >= 2 && args[0] == "image")
{
    var run0 = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(args[1]))!;
    if (run0.Parts.Count == 0) Report.ScoreParts(run0, suite); // a result from before the per-part scores
    foreach (var p in SaveImages(run0, Report.Previous(resultsDir, run0.Version), Path.GetDirectoryName(Path.GetFullPath(args[1]))!)) Console.WriteLine("이미지: " + p);
    return 0;
}
if (args.Length == 0 || args[0] != "run")
{
    Console.WriteLine("사용법: run [--launcher|--qutra] [--quick] [--reps N] [--exe 경로] [--only 접두어]  |  image <결과.json>");
    return 1;
}

bool quick = args.Contains("--quick");
string? exe = Opt("--exe");
bool launcher = !args.Contains("--qutra") || args.Contains("--launcher");
bool qutra = exe is null && (!args.Contains("--launcher") || args.Contains("--qutra"));
int reps = int.TryParse(Opt("--reps"), out var n) ? n : quick ? 1 : suite.Reps;
string? only = Opt("--only");
string version = exe is not null ? ExeVersion(exe) : Ver(typeof(SearchEngine).Assembly.GetName().Version!);

var all = Case.Load(Path.Combine(casesDir, "launcher")).Concat(Case.Load(Path.Combine(casesDir, "qutra"))).Concat(Case.Load(Path.Combine(casesDir, "holdout"), holdout: true))
    .Where(c => only is null || c.Id.StartsWith(only, StringComparison.Ordinal)).ToList();
var dupes = all.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
if (dupes.Count > 0) throw new InvalidDataException("같은 id: " + string.Join(", ", dupes));
bool IsQutra(Case c) => suite.Categories.TryGetValue(c.Cat, out var d) ? d.Part == "qutra" : throw new InvalidDataException($"{c.Id}: suite.json 에 없는 cat '{c.Cat}'");

var run = new RunResult
{
    Version = version, Suite = suite.Version, Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
    Mode = (exe is null ? "소스" : "exe") + (quick ? ", 빠른 실행" : "") + (only is null ? "" : $", {only}*"), Machine = Report.Machine(),
};
Action<string> log = Console.WriteLine;
var total = Stopwatch.StartNew();
double? warm = null;

using (ISearch search = exe is null ? new InProcessSearch() : new McpSearch(exe))
{
    if (search is InProcessSearch ip) await ip.Engine.WarmUpAsync();
    if (launcher)
    {
        var lc = all.Where(c => !IsQutra(c)).ToList();
        log($"런처 {lc.Count}개 ({search.Mode})");
        await search.SearchAsync("warm up", instant: false);
        run.Items.AddRange(await Launcher.RunAsync(search, lc, Path.Combine(root, "bench", "fixtures", "files.json"), log));
    }
    if (qutra)
    {
        var qc = all.Where(IsQutra).ToList();
        if (quick) qc = qc.GroupBy(c => c.Cat).SelectMany(g => g.Take(3)).ToList();
        if (!AiModel.IsAvailable) log("Qutra: 모델이 없어 건너뜀 (설정에서 내려받기, RAM 64GB 이상)");
        else
        {
            log($"\nQutra {qc.Count}개 × {reps}번");
            var (items, w) = await Qutra.RunAsync((InProcessSearch)search, qc, reps, log);
            run.Items.AddRange(items);
            warm = w;
        }
    }
}

// One part only (--launcher or --qutra): the other part's items come from this version's earlier full result, so a
// quick re-run of the launcher does not throw away an hour of Qutra.
var earlier = Path.Combine(resultsDir, $"v{version}.json");
if (launcher != qutra && !quick && only is null && exe is null && File.Exists(earlier))
{
    var old = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(earlier))!;
    bool OtherPart(ItemResult i) => suite.Categories.TryGetValue(i.Cat, out var d) && (d.Part == "qutra") != qutra;
    run.Items.AddRange(old.Items.Where(OtherPart));
    if (!qutra && old.Speed.FirstOrDefault(s => s.Name == "qutra-warmup") is { } w0) warm = w0.P50 * 1000;
    if (old.Items.Any(OtherPart)) log($"{(qutra ? "런처" : "Qutra")} 결과는 이전 실행({old.Date})의 것을 씀");
}

Report.Score(run, suite, warm);
var prev = quick ? null : Report.Previous(resultsDir, version);
Report.Alerts(run, prev, suite);
Console.WriteLine(Report.Table(run, prev, suite));

var outDir = quick || only is not null ? Path.Combine(resultsDir, "quick") : resultsDir;
Directory.CreateDirectory(outDir);
var jsonPath = Path.Combine(outDir, $"v{version}.json");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(run, RunResult.Json));
if (outDir == resultsDir) Report.UpdateHistory(Path.Combine(resultsDir, "history.json"), run);
var pngs = SaveImages(run, prev, outDir);
Console.WriteLine($"결과: {jsonPath}\n이미지: {string.Join(", ", pngs)}\n걸린 시간: {total.Elapsed.TotalMinutes:F1}분");
return 0;

// bench-v<version>-launcher.png and bench-v<version>-qutra.png, for the parts this result has.
List<string> SaveImages(RunResult r, RunResult? p, string dir)
{
    static JsonArray Read(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : [];
    var paths = new List<string>();
    foreach (var part in r.Parts.Keys)
    {
        // The launcher's graph shows every release, scored the same way over MCP, when those scores exist.
        var releases = Path.Combine(releasesDir, "history.json");
        bool byRelease = part == "launcher" && File.Exists(releases);
        var history = byRelease ? Read(releases) : Read(Path.Combine(resultsDir, "history.json"));
        var path = Path.Combine(dir, $"bench-v{r.Version}-{part}.png");
        Quicklight.Bench.Image.Save(path, r, p, suite, history, part, byRelease ? "릴리스별 점수 (MCP로 잰 공통 문항)" : "버전별 종합 점수");
        paths.Add(path);
    }
    return paths;
}

string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

static string Ver(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

static string ExeVersion(string exe)
{
    var v = FileVersionInfo.GetVersionInfo(exe);
    return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
}

static string FindRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "bench", "cases"))) return d.FullName;
    for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "bench", "cases"))) return d.FullName;
    throw new DirectoryNotFoundException("bench\\cases 를 찾지 못했습니다. 저장소 안에서 실행하세요.");
}
