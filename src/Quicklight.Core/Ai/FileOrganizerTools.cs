using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quicklight.Core.Ai;

/// <summary>
/// Lets the AI list and organize folders. Everything is confined to the user profile (not AppData), nothing is deleted or
/// overwritten (a clash gets " (2)"), the whole batch is validated before one consent, and every batch is journaled so it can be undone.
/// The tools are marked read-only only because this class asks the consent itself, after validating and before changing anything.
/// </summary>
public sealed class FileOrganizerTools(IConsent consent, string? root = null, string? journalPath = null) : IAgentTools
{
    public const int MaxOps = 1000, MaxList = 500, ShownLines = 15;

    readonly string _root = Path.GetFullPath(root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('\\', '/');
    readonly string _journal = journalPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "organize-journal.jsonl");

    public sealed record Op(string Kind, string From, string To);
    public sealed record Move(string From, string To);

    static JsonObject Schema(JsonObject props, params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
    };

    static JsonObject P(string type, string desc) => new() { ["type"] = type, ["description"] = desc };

    static readonly AgentTool[] All =
    [
        new("list_folder", "폴더의 파일과 하위 폴더(이름, 크기, 수정 시각)를 최대 500개까지 읽습니다. 정리하기 전에 먼저 호출하세요.",
            Schema(new() { ["path"] = P("string", "폴더의 절대 경로"), ["recursive"] = P("boolean", "하위 폴더까지 읽기 (기본 false)") }, "path"), true),
        new("organize", "파일/폴더를 옮기거나 이름을 바꾸거나 폴더를 만듭니다. 한꺼번에 모아서 한 번에 보내세요(최대 1000개). 삭제와 덮어쓰기는 없고, 이름이 겹치면 ' (2)'가 붙습니다. 사용자 확인은 이 도구가 한 번 받습니다.",
            Schema(new()
            {
                ["operations"] = new JsonObject
                {
                    ["type"] = "array", ["description"] = "작업 목록",
                    ["items"] = Schema(new()
                    {
                        ["op"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("move", "rename", "mkdir") },
                        ["from"] = P("string", "원본 절대 경로 (mkdir은 생략)"),
                        ["to"] = P("string", "새 절대 경로. rename은 새 이름만 써도 됨. 기존 폴더를 주면 그 안으로 옮김"),
                    }, "op", "to"),
                },
            }, "operations"), true),
        new("undo_last_organize", "가장 최근의 정리(organize)를 되돌립니다.", Schema([]), true),
    ];

    public IReadOnlyList<AgentTool> Tools => All;

    public string Describe(string name, JsonObject args) => name;

    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        try
        {
            return name switch // AiSession.Label wraps it as data
            {
                "list_folder" => List(args),
                "organize" => await OrganizeAsync(args, ct),
                "undo_last_organize" => await UndoAsync(ct),
                _ => "알 수 없는 도구: " + name,
            };
        }
        catch (ArgumentException ex) { return "오류: " + ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "파일 오류: " + ex.Message; }
    }

    // ---- safety ----

    /// <summary>The normalized path if it is allowed (absolute, inside the profile, not AppData, nothing hidden/system/reparse on the way); otherwise ArgumentException.</summary>
    public string Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("절대 경로여야 합니다: " + path);
        if (path.Split('\\', '/').Contains("..")) throw new ArgumentException("'..'는 쓸 수 없습니다: " + path);
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new ArgumentException("잘못된 경로입니다: " + path); }
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && !_root.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("네트워크/장치 경로는 쓸 수 없습니다.");
        if (Path.GetPathRoot(full)?.TrimEnd('\\', '/').Equals(full, StringComparison.OrdinalIgnoreCase) == true) throw new ArgumentException("드라이브 루트는 쓸 수 없습니다.");
        if (!Under(full, _root)) throw new ArgumentException("사용자 폴더 밖입니다: " + full);
        if (Under(full, Path.Combine(_root, "AppData"))) throw new ArgumentException("AppData는 건드릴 수 없습니다: " + full);
        // Every existing piece below the profile: a junction or hidden/system item anywhere on the way is refused.
        var cur = _root;
        foreach (var part in full[_root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, part);
            FileAttributes a;
            try { a = File.GetAttributes(cur); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { break; }
            if ((a & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) throw new ArgumentException("숨김/시스템/바로가기 항목은 건드릴 수 없습니다: " + cur);
        }
        return full;
    }

    static bool Under(string path, string dir) => path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || path.Equals(dir, StringComparison.OrdinalIgnoreCase);

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>"a.pdf" -> "a (2).pdf" -> "a (3).pdf" until <paramref name="taken"/> says it is free.</summary>
    public static string FreeName(string path, Func<string, bool> taken)
    {
        if (!taken(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 2; ; i++) { var c = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!taken(c)) return c; }
    }

    // ---- plan ----

    /// <summary>Validates every operation and works out the final destinations (clashes included, also between operations of the batch). Throws on the first bad one.</summary>
    public (List<Move> Moves, List<string> Dirs) Plan(IReadOnlyList<Op> ops)
    {
        if (ops.Count == 0) throw new ArgumentException("작업이 없습니다.");
        if (ops.Count > MaxOps) throw new ArgumentException($"한 번에 {MaxOps}개까지만 할 수 있습니다.");
        var moves = new List<Move>(); var dirs = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // destinations of this batch
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // sources already moved by this batch
        bool Taken(string p) => taken.Contains(p) || (Exists(p) && !gone.Contains(p));
        foreach (var op in ops)
        {
            if (op.Kind == "mkdir")
            {
                var d = Check(op.To);
                if (File.Exists(d)) throw new ArgumentException("같은 이름의 파일이 있습니다: " + d);
                if (!Directory.Exists(d) && taken.Add(d)) dirs.Add(d);
                continue;
            }
            if (op.Kind is not ("move" or "rename")) throw new ArgumentException("op는 move, rename, mkdir 중 하나여야 합니다: " + op.Kind);
            var from = Check(op.From);
            if (!Exists(from) || gone.Contains(from)) throw new ArgumentException("원본이 없습니다: " + from);
            var to = !op.To.Contains('\\') && !op.To.Contains('/') ? Path.Combine(Path.GetDirectoryName(from)!, op.To) : op.To;
            to = Check(to);
            if (Directory.Exists(to) || dirs.Contains(to, StringComparer.OrdinalIgnoreCase)) to = Check(Path.Combine(to, Path.GetFileName(from))); // into a folder
            if (Under(to, from)) throw new ArgumentException("폴더를 자기 안으로 옮길 수 없습니다: " + from);
            if (string.Equals(to, from, StringComparison.OrdinalIgnoreCase)) continue;
            to = Check(FreeName(to, Taken));
            moves.Add(new(from, to)); taken.Add(to); gone.Add(from);
        }
        return (moves, dirs);
    }

    static string Show(Move m)
    {
        var dir = Path.GetDirectoryName(m.From)!;
        return $"{Path.GetFileName(m.From)} → {(Under(m.To, dir) ? m.To[(dir.Length + 1)..] : m.To)}";
    }

    // ---- tools ----

    string List(JsonObject args)
    {
        var dir = Check(Str(args, "path"));
        if (!Directory.Exists(dir)) throw new ArgumentException("폴더가 없습니다: " + dir);
        bool recursive = args["recursive"] is JsonValue v && v.TryGetValue<bool>(out var r) && r;
        var sb = new StringBuilder(); int n = 0; bool more = false;
        var opts = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
        {
            if (n == MaxList) { more = true; break; }
            n++;
            var rel = e.FullName[(dir.Length + 1)..];
            sb.AppendLine(e is FileInfo f ? $"{rel}\t{f.Length}B\t{f.LastWriteTime:yyyy-MM-dd}" : $"{rel}\\\t폴더\t{e.LastWriteTime:yyyy-MM-dd}");
        }
        return n == 0 ? "비어 있습니다." : $"{dir} ({n}개{(more ? $", 처음 {MaxList}개만" : "")}):\n{sb}";
    }

    async Task<string> OrganizeAsync(JsonObject args, CancellationToken ct)
    {
        if (args["operations"] is not JsonArray arr) throw new ArgumentException("'operations'가 필요합니다.");
        var ops = arr.Select(o => new Op(Str(o as JsonObject, "op"), (o as JsonObject)?["from"] is JsonValue fv && fv.TryGetValue<string>(out var fs) ? fs : "", Str(o as JsonObject, "to"))).ToList();
        var (moves, dirs) = Plan(ops);
        if (moves.Count == 0 && dirs.Count == 0) return "바꿀 것이 없습니다.";
        var sb = new StringBuilder($"파일 {moves.Count}개 이동/이름 변경" + (dirs.Count > 0 ? $", 폴더 {dirs.Count}개 만들기" : "") + "\n");
        foreach (var l in dirs.Select(d => "새 폴더: " + d).Concat(moves.Select(Show)).Take(ShownLines)) sb.AppendLine(AgentDesktopTools.Clean(l, 200));
        if (moves.Count + dirs.Count > ShownLines) sb.AppendLine($"… 외 {moves.Count + dirs.Count - ShownLines}개");
        if (!await consent.ConfirmAsync(sb.ToString().TrimEnd(), ct)) return "사용자가 거절했습니다";

        var made = new List<string>(); var done = new List<Move>(); string? failure = null;
        try
        {
            foreach (var d in dirs) if (!Directory.Exists(d)) { Directory.CreateDirectory(d); made.Add(d); }
            foreach (var m in moves)
            {
                ct.ThrowIfCancellationRequested();
                var target = FreeName(m.To, Exists); // re-checked: the disk may have changed since the plan
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Directory.Exists(m.From)) Directory.Move(m.From, target); else File.Move(m.From, target, false);
                done.Add(m with { To = target });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { failure = ex.Message; }
        if (done.Count + made.Count > 0) Append(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["time"] = DateTime.Now.ToString("s"),
            ["moves"] = new JsonArray(done.Select(m => (JsonNode)new JsonObject { ["from"] = m.From, ["to"] = m.To }).ToArray()),
            ["dirs"] = new JsonArray(made.Select(d => (JsonNode)d).ToArray()),
        });
        return (failure is null ? $"파일 {done.Count}개를 옮겼습니다." : $"{done.Count}개를 옮긴 뒤 멈췄습니다: {failure}") + " 되돌리려면 '되돌려'라고 말하세요.";
    }

    async Task<string> UndoAsync(CancellationToken ct)
    {
        if (LastBatch() is not var (id, moves, dirs)) return "되돌릴 정리가 없습니다.";
        var plan = new List<Move>(); var skipped = new List<string>();
        foreach (var m in moves.AsEnumerable().Reverse())
        {
            try
            {
                var from = Check(m.From); var to = Check(m.To);
                if (Exists(to) && !Exists(from)) plan.Add(new(to, from)); else skipped.Add(Path.GetFileName(m.To));
            }
            catch (ArgumentException) { skipped.Add(Path.GetFileName(m.To)); }
        }
        var sb = new StringBuilder($"방금 정리를 되돌립니다: 파일 {plan.Count}개" + (skipped.Count > 0 ? $" (되돌릴 수 없는 {skipped.Count}개 제외)" : "") + "\n");
        foreach (var m in plan.Take(ShownLines)) sb.AppendLine(AgentDesktopTools.Clean(Show(m), 200));
        if (plan.Count > ShownLines) sb.AppendLine($"… 외 {plan.Count - ShownLines}개");
        if (!await consent.ConfirmAsync(sb.ToString().TrimEnd(), ct)) return "사용자가 거절했습니다";

        int back = 0;
        foreach (var m in plan)
        {
            try
            {
                if (Exists(m.To)) { skipped.Add(Path.GetFileName(m.From)); continue; } // taken since the plan: never overwrite
                Directory.CreateDirectory(Path.GetDirectoryName(m.To)!);
                if (Directory.Exists(m.From)) Directory.Move(m.From, m.To); else File.Move(m.From, m.To, false);
                back++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped.Add(Path.GetFileName(m.From)); }
        }
        foreach (var d in dirs.AsEnumerable().Reverse()) // folders the batch created, only when empty again
        {
            try { var c = Check(d); if (Directory.Exists(c) && !Directory.EnumerateFileSystemEntries(c).Any()) Directory.Delete(c); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        Append(new JsonObject { ["undo"] = id, ["time"] = DateTime.Now.ToString("s") });
        return $"파일 {back}개를 되돌렸습니다." + (skipped.Count > 0 ? $" 되돌리지 못한 {skipped.Count}개: {string.Join(", ", skipped.Take(10).Select(s => AgentDesktopTools.Clean(s, 60)))}" : "");
    }

    // ---- journal ----

    void Append(JsonObject line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journal)!);
        File.AppendAllText(_journal, line.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
    }

    /// <summary>The newest batch that has not been undone.</summary>
    (string Id, List<Move> Moves, List<string> Dirs)? LastBatch()
    {
        if (!File.Exists(_journal)) return null;
        var undone = new HashSet<string>(); JsonObject? last = null;
        foreach (var line in File.ReadLines(_journal).Reverse())
        {
            try
            {
                if (JsonNode.Parse(line) is not JsonObject o) continue;
                if (o["undo"]?.GetValue<string>() is { } u) undone.Add(u);
                else if (o["id"]?.GetValue<string>() is { } id && !undone.Contains(id)) { last = o; break; }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        if (last is null) return null;
        return (last["id"]!.GetValue<string>(),
            last["moves"]!.AsArray().Select(m => new Move(m!["from"]!.GetValue<string>(), m["to"]!.GetValue<string>())).ToList(),
            last["dirs"]!.AsArray().Select(d => d!.GetValue<string>()).ToList());
    }

    static string Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new ArgumentException($"'{key}'가 필요합니다.");
}
