using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quicklight.Core.Providers;

namespace Quicklight.Core.Ai;

/// <summary>
/// Computer control for the AI: a few agent-desktop commands as tools. Looking (snapshot, find, screenshot, windows) is
/// read-only; click, set-value, press and launch change things and so need the user's consent. Never passes --force
/// (so dangerous shortcuts stay refused), and needs no --headed: it is only required by <c>type</c> and
/// <c>right-click</c>, which are not offered (set-value writes the text instead).
/// </summary>
public sealed partial class AgentDesktopTools : IAgentTools
{
    public const int MaxChars = 5000;

    // One namespace for the refs of this run, apart from anything else using agent-desktop.
    readonly string _session = "quicklight-" + Environment.ProcessId;
    readonly Dictionary<string, string> _labels = []; // ref -> "버튼 '저장'", from what the model has looked at: the consent line names the element

    static readonly AgentTool[] All =
    [
        new("ui_list_windows", "열려 있는 창 목록(앱 이름, 제목, 창 id)을 읽습니다.", Schema([]), true),
        new("ui_snapshot", "앱 창의 UI 요소 트리를 읽습니다. 클릭할 수 있는 요소에는 ref(@s123:e4)가 붙습니다. 큰 앱은 skeleton=true로 먼저 훑고, 필요한 부분의 ref를 root로 다시 읽으세요.",
            Schema([("app", "string", "앱 이름 (root를 쓰면 생략)"), ("root", "string", "여기서부터 읽을 ref"), ("skeleton", "boolean", "얕게(깊이 3)만 읽기")]), true),
        new("ui_find", "앱에서 역할과 이름으로 UI 요소를 찾아 ref를 얻습니다.",
            Schema([("app", "string", "앱 이름"), ("role", "string", "button, textfield, menuitem 등"), ("name", "string", "요소 이름(일부 일치)")], "app"), true),
        new("ui_screenshot", "화면(app을 주면 그 앱 창)을 PNG로 저장하고 파일 경로를 알려 줍니다.", Schema([("app", "string", "앱 이름 (생략하면 전체 화면)")]), true),
        new("ui_click", "ref 요소를 클릭합니다.", Schema([("ref", "string", "ui_snapshot/ui_find가 준 ref")], "ref"), false),
        new("ui_set_value", "텍스트 입력란(ref)의 내용을 주어진 텍스트로 바꿉니다.",
            Schema([("ref", "string", "입력란 ref"), ("text", "string", "넣을 텍스트")], "ref", "text"), false),
        new("ui_press", "키 조합을 누릅니다. 예: return, tab, ctrl+s. 위험한 단축키(alt+f4, win+r 등)는 거절됩니다.",
            Schema([("keys", "string", "키 조합"), ("app", "string", "대상 앱 이름 (생략하면 맨 앞 창)")], "keys"), false),
        new("ui_launch", "프로그램을 실행합니다. 실행 파일의 전체 경로가 필요합니다.", Schema([("path", "string", "절대 경로")], "path"), false),
    ];

    public IReadOnlyList<AgentTool> Tools => AgentDesktop.CheckAvailable() is null ? All : [];

    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        string[]? cli;
        try { cli = Command(name, args); }
        catch (ArgumentException ex) { return "인자 오류: " + ex.Message; }
        if (cli is null) return $"알 수 없는 도구: {name}";

        var r = await AgentDesktop.RunAsync(["--session", _session, .. cli], ct);
        if (r.Raw.Length == 0) return $"오류 {r.ErrorCode}: {r.ErrorMessage}";
        if (r.Ok && r.Data is not null) Remember(r.Data);
        if (name == "ui_screenshot" && r.Ok) return "스크린샷을 저장했습니다: " + cli[^1];
        return Truncate(r.Raw);
    }

    /// <summary>The agent-desktop arguments for a tool call; ArgumentException for a bad argument, null for an unknown tool.</summary>
    string[]? Command(string name, JsonObject args)
    {
        switch (name)
        {
            case "ui_list_windows": return ["list-windows"];
            case "ui_snapshot":
                var snap = new List<string> { "snapshot" };
                if (Opt(args, "root") is { } root) snap.AddRange(["--root", Ref(root)]);
                else snap.AddRange(["--app", Req(args, "app")]);
                if (args["skeleton"] is JsonValue s && s.TryGetValue<bool>(out var skeleton) && skeleton) snap.Add("--skeleton");
                snap.AddRange(["-i", "--compact"]);
                return [.. snap];
            case "ui_find":
                var find = new List<string> { "find", "--app", Req(args, "app") };
                if (Opt(args, "role") is { } role) find.AddRange(["--role", Safe(role, "role")]);
                if (Opt(args, "name") is { } nm) find.AddRange(["--name", Safe(nm, "name")]);
                find.AddRange(["--limit", "30"]);
                return [.. find];
            case "ui_screenshot":
                var shots = Path.Combine(AgentDesktop.Home, "screenshots");
                Directory.CreateDirectory(shots);
                foreach (var old in Directory.EnumerateFiles(shots).Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-1))) try { File.Delete(old); } catch (IOException) { } // screens hold private things
                var file = Path.Combine(shots, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                return Opt(args, "app") is { } app ? ["screenshot", "--app", Safe(app, "app"), file] : ["screenshot", file];
            case "ui_click": return ["click", Ref(Req(args, "ref"))];
            case "ui_set_value": return ["set-value", Ref(Req(args, "ref")), "--", Text(args, "text")]; // "--": text starting with "-" is still the value
            case "ui_press":
                var press = new List<string> { "press", Req(args, "keys") };
                if (Opt(args, "app") is { } pa) press.AddRange(["--app", Safe(pa, "app")]);
                return [.. press];
            case "ui_launch":
                var path = Req(args, "path");
                if (!Path.IsPathFullyQualified(path) || PathProvider.IsRemoteOrDevice(path) || OnNetworkDrive(path)) throw new ArgumentException("path는 로컬 절대 경로여야 합니다.");
                if (!File.Exists(path)) throw new ArgumentException("파일이 없습니다: " + path);
                return ["launch", path];
            default: return null;
        }
    }

    /// <summary>A mapped or substituted drive is the network too, which "\\host" checks do not see.</summary>
    static bool OnNetworkDrive(string path)
    {
        try { return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network; }
        catch (ArgumentException) { return false; }
    }

    static readonly string[] AppRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
    ];

    public string Describe(string name, JsonObject args)
    {
        string S(string key) => Opt(args, key) ?? "";
        string Short(string s) => Clean(s, 500); // the whole text that will be typed, not its harmless start
        return name switch
        {
            "ui_click" => $"클릭: {Label(S("ref"))}",
            "ui_set_value" => $"{Label(S("ref"))}에 '{Short(S("text"))}' 입력",
            "ui_press" => $"키 입력: {Clean(S("keys"), 100)}" + (S("app").Length > 0 ? $" ({Clean(S("app"), 60)})" : ""),
            "ui_launch" => $"프로그램 실행: {Clean(S("path"), 300)}" + (AppRoots.Any(r => r.Length > 0 && S("path").StartsWith(r, StringComparison.OrdinalIgnoreCase)) ? "" : " — 설치된 앱 폴더 밖의 파일입니다"),
            "ui_snapshot" => $"화면 읽기: {S("app")}{S("root")}",
            "ui_find" => $"요소 찾기: {S("app")}",
            "ui_screenshot" => "스크린샷 찍기",
            "ui_list_windows" => "열린 창 목록 읽기",
            _ => name,
        };
    }

    // The names come from what is on screen, so they are shown as such: a page can name a button "취소".
    string Label(string @ref) => _labels.TryGetValue(@ref, out var l) ? $"{l} ({@ref})" : @ref;

    /// <summary>Notes "role 'name'" for every element with a ref in a snapshot or find result.</summary>
    void Remember(JsonNode node)
    {
        if (_labels.Count > 5000) _labels.Clear();
        if (node is JsonObject o)
        {
            if (o["ref_id"]?.GetValue<string>() is { } id) _labels[id] = $"{Clean(o["role"]?.GetValue<string>() ?? "", 30)} (화면 표시 이름 '{Clean(o["name"]?.GetValue<string>() ?? "", 60)}')";
            foreach (var child in o.Select(p => p.Value)) if (child is not null) Remember(child);
        }
        else if (node is JsonArray a) foreach (var child in a) if (child is not null) Remember(child);
    }

    // ---- arguments ----

    /// <summary>Text for the consent line: control and direction-changing characters made visible or removed, cut to <paramref name="max"/>.</summary>
    internal static string Clean(string text, int max)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c is '\n' or '\r') sb.Append('⏎');
            else if (c == '\t') sb.Append(' ');
            else if (char.IsControl(c) || c is >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\uFEFF') continue;
            else sb.Append(c);
        }
        var clean = sb.ToString();
        return clean.Length > max ? clean[..max] + $"… (총 {text.Length}자)" : clean;
    }

    static string? Opt(JsonObject args, string key) => args[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    static string Req(JsonObject args, string key) => Safe(Opt(args, key) ?? throw new ArgumentException($"'{key}'가 필요합니다."), key);

    /// <summary>A model-made value that starts with "-" could be read by the program as an option ("--force").</summary>
    static string Safe(string v, string key) => v.StartsWith('-') ? throw new ArgumentException($"'{key}'는 '-'로 시작할 수 없습니다.") : v;

    static string Text(JsonObject args, string key) => args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException($"'{key}'가 필요합니다.");

    /// <summary>Only a real ref, so a model-made value can never be read as an option.</summary>
    static string Ref(string r) => RefPattern().IsMatch(r) ? r : throw new ArgumentException("ref는 @s123:e4 형태여야 합니다.");

    [GeneratedRegex(@"^@[A-Za-z0-9]+:e\d+$")]
    private static partial Regex RefPattern();

    static JsonObject Schema((string Name, string Type, string Description)[] props, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(props.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, new JsonObject { ["type"] = p.Type, ["description"] = p.Description }))),
        ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
    };

    // ---- output size ----

    static readonly JsonSerializerOptions Wire = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Keeps a result within <paramref name="max"/> characters. A snapshot (data.tree) loses its deepest levels first,
    /// so what is left is still whole JSON; anything else is cut and marked "[잘림]".
    /// </summary>
    public static string Truncate(string text, int max = MaxChars)
    {
        if (text.Length <= max) return text;
        try
        {
            if (JsonNode.Parse(text) is JsonObject root && root["data"]?["tree"] is JsonObject tree)
            {
                for (int keep = Depth(tree) - 1; keep >= 1; keep--)
                {
                    Prune(tree, 1, keep);
                    root["data"]!["[잘림]"] = "깊은 요소를 생략했습니다. 필요하면 해당 ref를 root로 ui_snapshot을 다시 호출하세요.";
                    var json = root.ToJsonString(Wire);
                    if (json.Length <= max) return json;
                }
            }
        }
        catch (JsonException) { }
        return text[..max] + "\n[잘림]";
    }

    static int Depth(JsonObject node) => 1 + (node["children"] is JsonArray c ? c.OfType<JsonObject>().Select(Depth).DefaultIfEmpty(0).Max() : 0);

    /// <summary>Drops the children of the nodes at depth <paramref name="keep"/>, leaving a count of what was dropped.</summary>
    static void Prune(JsonObject node, int level, int keep)
    {
        if (node["children"] is not JsonArray children) return;
        if (level >= keep) { node.Remove("children"); node["children_omitted"] = children.Count; return; }
        foreach (var child in children.OfType<JsonObject>()) Prune(child, level + 1, keep);
    }
}
