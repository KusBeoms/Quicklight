using System.Text;
using Microsoft.Win32;
using Quicklight.Core.Ai;

namespace Quicklight.Bench;

/// <summary>Scores, the comparison with the previous version, history.json and the console table.</summary>
static class Report
{
    public static void Score(RunResult run, Suite suite, double? warmUpMs)
    {
        foreach (var (id, def) in suite.Categories)
        {
            var items = run.Items.Where(i => i.Cat == id && !i.Holdout).ToList();
            if (items.Count == 0) continue;
            run.Categories[id] = Category(def.Name, items);
        }
        var holdout = run.Items.Where(i => i.Holdout).ToList();
        if (holdout.Count > 0) run.Categories["holdout"] = Category("숨김 세트 (Qutra)", holdout);

        Speed(run, suite, warmUpMs);
        if (run.Speed.Count > 0)
            foreach (var part in new[] { "launcher", "qutra" })
                if (run.Speed.Where(s => s.Name.StartsWith(part)).ToList() is { Count: > 0 } ss)
                    run.Categories[part + "-speed"] = new CategoryScore { Name = part == "launcher" ? "런처 속도" : "Qutra 속도", Score = Math.Round(ss.Average(s => s.Score), 1), Items = ss.Count };

        ScoreParts(run, suite);
    }

    /// <summary>Weighted over the categories this run has (a part that did not run is left out): overall, and per part.</summary>
    public static void ScoreParts(RunResult run, Suite suite)
    {
        (run.Overall, run.OverallCi) = Weighted(run, suite, null);
        foreach (var part in new[] { "launcher", "qutra" })
            if (run.Categories.Keys.Any(id => PartOf(id, suite) == part))
            {
                var (s, ci) = Weighted(run, suite, part);
                run.Parts[part] = s;
                run.PartsCi[part] = ci;
            }
    }

    /// <summary>launcher | qutra for a category id (the speed and hidden-set rows included).</summary>
    public static string PartOf(string id, Suite suite) => id switch
    {
        "holdout" => "qutra",
        _ => suite.Categories.TryGetValue(id, out var d) ? d.Part : id.StartsWith("qutra") ? "qutra" : "launcher",
    };

    static (double Score, double Ci) Weighted(RunResult run, Suite suite, string? part)
    {
        double sum = 0, weights = 0, var = 0;
        foreach (var (id, cs) in run.Categories)
        {
            if (!suite.Categories.TryGetValue(id, out var def) || def.Weight <= 0 || part is not null && def.Part != part) continue;
            sum += cs.Score * def.Weight; weights += def.Weight;
            var += Math.Pow(cs.Ci / 1.96 * def.Weight, 2);
        }
        return weights > 0 ? (Math.Round(sum / weights, 1), Math.Round(1.96 * Math.Sqrt(var) / weights, 1)) : (0, 0);
    }

    static CategoryScore Category(string name, List<ItemResult> items)
    {
        // Per repetition: the share of items that passed in that run; their spread is the noise.
        int reps = items.Max(i => i.Passes.Count);
        var perRep = Enumerable.Range(0, reps).Select(r => items.Average(i => r < i.Passes.Count ? (i.Passes[r] ? 1.0 : 0) : i.Pass) * 100).ToList();
        double mean = items.Average(i => i.Pass) * 100;
        double ci = reps > 1 ? 1.96 * Std(perRep) / Math.Sqrt(reps) : 0;
        return new CategoryScore
        {
            Name = name, Score = Math.Round(mean, 1), Ci = Math.Round(ci, 1), Items = items.Count,
            Tiers = items.GroupBy(i => i.Tier).ToDictionary(g => g.Key, g => Math.Round(g.Average(i => i.Pass) * 100, 1)),
        };
    }

    static double Std(List<double> xs)
    {
        double m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / Math.Max(1, xs.Count - 1));
    }

    static void Speed(RunResult run, Suite suite, double? warmUpMs)
    {
        void Add(string id, string name, IEnumerable<double> values, string unit = "ms", bool higher = false)
        {
            var v = values.ToList();
            if (v.Count == 0 || !suite.Targets.TryGetValue(id, out var target)) return;
            double p50 = Pct(v, 0.5), p95 = Pct(v, 0.95);
            double score = higher ? Math.Min(100, 100 * p50 / target) : Math.Min(100, 100 * target / Math.Max(p95, 0.001));
            run.Speed.Add(new SpeedStat { Name = id, Unit = unit, P50 = Math.Round(p50, 1), P95 = Math.Round(p95, 1), Target = target, HigherIsBetter = higher, Score = Math.Round(score, 1) });
        }
        string[] instant = ["calc", "unit", "date", "currency", "timer", "system", "web"];
        Add("launcher-instant", "즉시 답", run.Items.Where(i => instant.Contains(i.Cat) && i.InstantMs is not null).Select(i => i.InstantMs!.Value));
        Add("launcher-app", "앱 찾기", run.Items.Where(i => i.Cat == "app" && i.Ms is not null).Select(i => i.Ms!.Value));
        Add("launcher-file", "파일 찾기", run.Items.Where(i => i.Cat == "file" && i.Ms is not null).Select(i => i.Ms!.Value));
        var qutra = run.Items.Where(i => suite.Categories.TryGetValue(i.Cat, out var d) && d.Part == "qutra").ToList();
        Add("qutra-oneshot", "단발 작업 답", qutra.Where(i => i.Cat is "task" && i.Ms is not null).Select(i => i.Ms!.Value / 1000), "s");
        Add("qutra-chat", "대화 답", qutra.Where(i => i.Cat is "tool" or "chat" or "fact" && i.Ms is not null).Select(i => i.Ms!.Value / 1000), "s");
        Add("qutra-tps", "초당 토큰", qutra.Where(i => i.TokensPerSecond is not null).Select(i => i.TokensPerSecond!.Value), "tok/s", higher: true);
        if (warmUpMs is { } w) Add("qutra-warmup", "모델 준비", [w / 1000], "s");
    }

    static double Pct(List<double> v, double p)
    {
        var s = v.Order().ToList();
        return s[(int)Math.Clamp(Math.Ceiling(p * s.Count) - 1, 0, s.Count - 1)];
    }

    public static Dictionary<string, string> Machine()
    {
        string Reg(string key, string value) => Registry.LocalMachine.OpenSubKey(key)?.GetValue(value)?.ToString()?.Trim() ?? "?";
        var m = new Dictionary<string, string>
        {
            ["cpu"] = Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString"),
            ["gpu"] = Gpu(),
            ["ram"] = $"{AiModel.RamBytes / (1L << 30)}GB",
            ["os"] = Environment.OSVersion.Version.ToString(),
        };
        if (File.Exists(AiModel.ModelPath)) m["model"] = $"{AiModel.ModelFile} ({new FileInfo(AiModel.ModelPath).Length >> 20}MB)";
        if (File.Exists(AiModel.ServerPath)) m["llama"] = $"{new FileInfo(AiModel.ServerPath).Length} bytes, {File.GetLastWriteTime(AiModel.ServerPath):yyyy-MM-dd}";
        return m;
    }

    /// <summary>The display adapters' names, virtual ones (remote desktop, VR, basic display) left out.</summary>
    static string Gpu()
    {
        const string Class = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var key = Registry.LocalMachine.OpenSubKey(Class);
        var names = (key?.GetSubKeyNames() ?? []).Where(n => n.All(char.IsDigit))
            .Select(n => key!.OpenSubKey(n)?.GetValue("DriverDesc")?.ToString()?.Trim())
            .Where(d => d is { Length: > 0 } && !d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !d.Contains("Basic", StringComparison.OrdinalIgnoreCase) && !d.Contains("Remote", StringComparison.OrdinalIgnoreCase) && !d.Contains("IDD", StringComparison.Ordinal))
            .Distinct().ToList();
        return names.Count == 0 ? "?" : string.Join(" + ", names);
    }

    public static bool SameMachine(RunResult a, RunResult b) =>
        new[] { "cpu", "gpu", "ram", "model", "llama" }.All(k => a.Machine.GetValueOrDefault(k) == b.Machine.GetValueOrDefault(k));

    /// <summary>The newest result of an earlier version (the run being compared with).</summary>
    public static RunResult? Previous(string dir, string version)
    {
        var mine = Version.Parse(version);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "v*.json")
                .Select(f => (File: f, V: Version.TryParse(Path.GetFileNameWithoutExtension(f)[1..], out var v) ? v : null))
                .Where(x => x.V is not null && x.V < mine).OrderByDescending(x => x.V)
                .Select(x => JsonSerializer.Deserialize<RunResult>(File.ReadAllText(x.File)))
                .FirstOrDefault()
            : null;
    }

    /// <summary>▲/▼ only when the change is outside both runs' noise (and at least 1 point); otherwise ≈.</summary>
    public static string Delta(double now, double then, double ciNow, double ciThen)
    {
        double d = now - then;
        if (Math.Abs(d) < Math.Max(1, ciNow + ciThen)) return "≈";
        return (d > 0 ? "▲" : "▼") + Math.Abs(d).ToString("0.#");
    }

    public static void Alerts(RunResult run, RunResult? prev, Suite suite)
    {
        bool IsQutra(ItemResult i) => suite.Categories.TryGetValue(i.Cat, out var d) && d.Part == "qutra";
        if (prev is not null && !SameMachine(run, prev)) run.Alerts.Add("이전 결과와 PC 또는 모델이 달라 속도는 비교하지 않음");
        if (prev is not null && prev.Suite != run.Suite) run.Alerts.Add($"문항 세트가 바뀜 (suite {prev.Suite} → {run.Suite}): 이전 점수와 직접 비교할 수 없음");
        foreach (var (id, c) in run.Categories)
            if (c.Score >= 95 && prev?.Categories.GetValueOrDefault(id) is { Score: >= 95 }) run.Alerts.Add($"{c.Name}: 두 번 연속 95점 이상, 어려운 문항 필요");
        double pub = run.Items.Where(i => !i.Holdout && IsQutra(i)).Select(i => i.Pass).DefaultIfEmpty(-1).Average();
        if (run.Categories.TryGetValue("holdout", out var h) && pub >= 0 && pub * 100 - h.Score > 15)
            run.Alerts.Add($"숨김 세트가 공개 문항보다 {pub * 100 - h.Score:F0}점 낮음: 프롬프트가 문항에 맞춰졌을 수 있음");
    }

    public static void UpdateHistory(string path, RunResult run)
    {
        var list = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : [];
        foreach (var old in list.Where(e => e?["version"]?.GetValue<string>() == run.Version).ToList()) list.Remove(old);
        list.Add(new JsonObject
        {
            ["version"] = run.Version, ["date"] = run.Date, ["suite"] = run.Suite, ["mode"] = run.Mode, ["overall"] = run.Overall,
            ["parts"] = new JsonObject(run.Parts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["categories"] = new JsonObject(run.Categories.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.Score))),
        });
        var sorted = list.OrderBy(e => Version.Parse(e!["version"]!.GetValue<string>())).Select(e => e!.DeepClone()).ToArray();
        File.WriteAllText(path, new JsonArray(sorted).ToJsonString(RunResult.Json));
    }

    public static string Table(RunResult run, RunResult? prev, Suite suite)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\nQuicklight 평가 v{run.Version}  (suite {run.Suite}, {run.Mode}{(prev is null ? "" : $", 이전 v{prev.Version}")})");
        sb.AppendLine(new string('─', 72));
        foreach (var (id, c) in run.Categories.OrderBy(kv => Order(kv.Key, suite)))
        {
            var p = prev?.Categories.GetValueOrDefault(id);
            string tiers = string.Join(" ", new[] { "easy", "normal", "hard" }.Where(c.Tiers.ContainsKey).Select(t => $"{TierName(t)} {c.Tiers[t]:0}"));
            sb.AppendLine($"{c.Name,-18} {c.Score,5:0.0}{(c.Ci > 0 ? $" ±{c.Ci:0.0}" : ""),-7} {(p is null ? "" : Delta(c.Score, p.Score, c.Ci, p.Ci)),-6} {(c.Items > 0 ? $"{c.Items}개" : ""),-6} {tiers}");
        }
        sb.AppendLine(new string('─', 72));
        foreach (var (part, score) in run.Parts)
        {
            double ci = run.PartsCi.GetValueOrDefault(part);
            string d = prev?.Parts.TryGetValue(part, out var ps) == true ? Delta(score, ps, ci, prev.PartsCi.GetValueOrDefault(part)) : "";
            sb.AppendLine($"{PartName(part) + " 종합",-18} {score,5:0.0}{(ci > 0 ? $" ±{ci:0.0}" : ""),-7} {d}");
        }
        sb.AppendLine($"{"종합",-18} {run.Overall,5:0.0}{(run.OverallCi > 0 ? $" ±{run.OverallCi:0.0}" : ""),-7} {(prev is null ? "" : Delta(run.Overall, prev.Overall, run.OverallCi, prev.OverallCi))}");
        if (run.Speed.Count > 0)
        {
            sb.AppendLine("\n속도");
            foreach (var s in run.Speed)
                sb.AppendLine($"  {SpeedName(s.Name),-14} p50 {s.P50,8:0.#} {s.Unit,-5} p95 {s.P95,8:0.#} {s.Unit,-5} 목표 {(s.HigherIsBetter ? "≥" : "p95 ≤")} {s.Target} → {s.Score:0}");
        }
        foreach (var a in run.Alerts) sb.AppendLine("! " + a);
        return sb.ToString();
    }

    public static int Order(string id, Suite suite) => id switch
    {
        "launcher-speed" => 500, "qutra-speed" => 1500, "holdout" => 1600,
        _ => (suite.Categories.TryGetValue(id, out var d) && d.Part == "qutra" ? 1000 : 0) + suite.Categories.Keys.ToList().IndexOf(id),
    };

    public static string PartName(string part) => part == "qutra" ? "Qutra" : "런처";

    public static string TierName(string t) => t switch { "easy" => "쉬움", "normal" => "보통", "hard" => "어려움", _ => t };

    public static string SpeedName(string id) => id switch
    {
        "launcher-instant" => "즉시 답", "launcher-app" => "앱 찾기", "launcher-file" => "파일 찾기",
        "qutra-oneshot" => "단발 작업", "qutra-chat" => "대화", "qutra-tps" => "초당 토큰", "qutra-warmup" => "모델 준비", _ => id,
    };

    /// <summary>The items that moved most against the previous run (for the image and the console).</summary>
    public static (List<(string Id, double D)> Drops, List<(string Id, double D)> Gains) Movers(RunResult run, RunResult? prev)
    {
        if (prev is null) return ([], []);
        var before = prev.Items.ToDictionary(i => i.Id, i => i.Pass);
        var d = run.Items.Where(i => before.ContainsKey(i.Id)).Select(i => (i.Id, D: (i.Pass - before[i.Id]) * 100)).Where(x => Math.Abs(x.D) >= 1).ToList();
        return (d.Where(x => x.D < 0).OrderBy(x => x.D).Take(5).ToList(), d.Where(x => x.D > 0).OrderByDescending(x => x.D).Take(5).ToList());
    }
}
