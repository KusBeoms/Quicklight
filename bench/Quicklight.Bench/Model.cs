using System.Globalization;
using System.Text.Encodings.Web;

namespace Quicklight.Bench;

/// <summary>One test item from bench\cases (a JSON line). Everything but these fields stays in <see cref="Raw"/>.</summary>
sealed record Case(string Id, string Cat, string Tier, bool Holdout, JsonObject Raw)
{
    public string? Str(string key) => Raw[key]?.GetValue<string>();
    public JsonObject? Obj(string key) => Raw[key] as JsonObject;

    public static IEnumerable<Case> Load(string dir, bool holdout = false)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.jsonl").Order())
        {
            int n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("//")) continue;
                JsonObject o;
                try { o = JsonNode.Parse(t)!.AsObject(); }
                catch (JsonException ex) { throw new InvalidDataException($"{Path.GetFileName(file)}:{n}: {ex.Message}"); }
                yield return new Case(o["id"]!.GetValue<string>(), o["cat"]!.GetValue<string>(), o["tier"]?.GetValue<string>() ?? "normal",
                    holdout || o["holdout"]?.GetValue<bool>() == true, o);
            }
        }
    }
}

/// <summary>A case's outcome: one pass/fail per repetition, the last answer, and timing.</summary>
sealed class ItemResult
{
    public string Id { get; set; } = "";
    public string Cat { get; set; } = "";
    public string Tier { get; set; } = "";
    public bool Holdout { get; set; }
    public List<bool> Passes { get; set; } = [];
    public double Pass => Passes.Count == 0 ? 0 : Passes.Count(p => p) / (double)Passes.Count;
    public string Answer { get; set; } = "";
    public string? Why { get; set; }
    public double? Ms { get; set; }
    public double? InstantMs { get; set; }
    public double? TokensPerSecond { get; set; }
}

sealed class CategoryScore
{
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public double Ci { get; set; }   // 95% range from the per-repetition scores (0 for single runs)
    public int Items { get; set; }
    public Dictionary<string, double> Tiers { get; set; } = [];
}

sealed class SpeedStat
{
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "ms";
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double Target { get; set; }
    public bool HigherIsBetter { get; set; }
    public double Score { get; set; }
}

sealed class RunResult
{
    public string Version { get; set; } = "";
    public int Suite { get; set; }
    public string Date { get; set; } = "";
    public string Mode { get; set; } = "";
    public Dictionary<string, string> Machine { get; set; } = [];
    public List<ItemResult> Items { get; set; } = [];
    public Dictionary<string, CategoryScore> Categories { get; set; } = [];
    public List<SpeedStat> Speed { get; set; } = [];
    public double Overall { get; set; }
    public double OverallCi { get; set; }
    public Dictionary<string, double> Parts { get; set; } = [];   // launcher / qutra: each part's own weighted score
    public Dictionary<string, double> PartsCi { get; set; } = [];
    public List<string> Alerts { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>bench\cases\suite.json: the test-set version, category names and weights, speed targets, repetitions.</summary>
sealed class Suite
{
    public int Version { get; set; }
    public int Reps { get; set; } = 5;
    public Dictionary<string, CategoryDef> Categories { get; set; } = [];
    public Dictionary<string, double> Targets { get; set; } = [];

    public sealed class CategoryDef
    {
        public string Name { get; set; } = "";
        public string Part { get; set; } = ""; // launcher | qutra
        public double Weight { get; set; }
    }

    public static Suite Load(string path) =>
        JsonSerializer.Deserialize<Suite>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}

static class Text
{
    static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>For "contains" checks: lower case, no spaces or thousands separators ("2,100 원" matches "2100원").</summary>
    public static string Norm(string s) => s.ToLowerInvariant().Replace(",", "").Replace(" ", "").Replace(" ", "");

    /// <summary>{today}, {today+3}, {today-1} as yyyy-MM-dd; {date+100:yyyy년 M월 d일} with a format (Korean culture).</summary>
    public static string Expand(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\{(today|date)([+-]\d+)?(?::([^}]+))?\}", m =>
    {
        var d = DateTime.Today.AddDays(m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0);
        return d.ToString(m.Groups[3].Success ? m.Groups[3].Value : "yyyy-MM-dd", Ko);
    });

    public static string Flat(string s, int max = 400)
    {
        var f = s.Replace("\r", "").Replace("\n", " ⏎ ");
        return f.Length > max ? f[..max] + "…" : f;
    }
}
