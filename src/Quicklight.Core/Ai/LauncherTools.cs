using System.Globalization;
using System.Text.Json.Nodes;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;

namespace Quicklight.Core.Ai;

/// <summary>
/// Launcher features for the AI: timers/reminders (<see cref="TimerService"/>) and quick controls (<see cref="QuickControls"/>).
/// The harmless ones run at once; cancel_timer is not ReadOnly (the session asks), turning Bluetooth/Wi-Fi off asks here
/// (system_control is one tool, so the ReadOnly flag cannot tell its actions apart).
/// </summary>
public sealed class LauncherTools(Func<string, CancellationToken, Task<bool>>? confirm = null, Func<DateTime>? now = null,
    Action<DateTime, string>? schedule = null, Func<IReadOnlyList<PendingTimer>>? pending = null, Action<string>? cancel = null, Action<string>? control = null) : IAgentTools
{
    static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    static readonly string[] Actions = ["volume_set", "volume_change", "mute", "unmute", "dark_mode", "light_mode", "bluetooth_on", "bluetooth_off", "wifi_on", "wifi_off"];

    static JsonObject Prop(string type, string desc) => new() { ["type"] = type, ["description"] = desc };
    static JsonObject Schema(JsonObject props, params string[] required) =>
        new() { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()) };

    static readonly AgentTool[] All =
    [
        new("set_timer", "타이머를 시작합니다. 시간이 되면 알림이 옵니다. minutes 또는 seconds 중 하나를 주세요.",
            Schema(new() { ["minutes"] = Prop("number", "분"), ["seconds"] = Prop("number", "초"), ["label"] = Prop("string", "알림에 보일 이름 (생략하면 '타이머')") }), true),
        new("set_reminder", "지정한 시각이나 몇 분 뒤에 알림을 예약합니다. at(로컬 시각 ISO 8601) 또는 minutes_from_now 중 하나를 주세요.",
            Schema(new() { ["at"] = Prop("string", "로컬 시각, 예: 2026-01-31T15:10"), ["minutes_from_now"] = Prop("number", "지금부터 몇 분 뒤"), ["message"] = Prop("string", "알림 내용") }, "message"), true),
        new("list_timers", "예약된 타이머와 알림 목록(id, 시각, 내용)을 읽습니다.", Schema([]), true),
        new("cancel_timer", "예약된 타이머/알림을 id로 취소합니다. id는 list_timers가 알려 줍니다.", Schema(new() { ["id"] = Prop("string", "타이머 id") }, "id"), false),
        new("system_control", "볼륨, 음소거, 다크/라이트 모드, 블루투스, Wi-Fi를 조절합니다. 볼륨 설정/변경에는 value(설정 0~100, 변경 -100~100)가 필요합니다.",
            Schema(new()
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Actions.Select(a => (JsonNode)a).ToArray()), ["description"] = "수행할 동작" },
                ["value"] = Prop("number", "volume_set은 0~100, volume_change는 -100~100"),
            }, "action"), true),
    ];

    public IReadOnlyList<AgentTool> Tools => All;

    DateTime Now => (now ?? (() => DateTime.Now))();

    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        try
        {
            switch (name)
            {
                case "set_timer":
                    double secs = Num(args, "seconds") ?? (Num(args, "minutes") is { } m ? m * 60 : throw new ArgumentException("minutes 또는 seconds가 필요합니다."));
                    return Schedule(Now.AddSeconds(Math.Round(secs)), Str(args, "label") ?? "타이머", secs);
                case "set_reminder":
                    var msg = Str(args, "message") ?? throw new ArgumentException("message가 필요합니다.");
                    if (Str(args, "at") is { } at)
                        return DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var due)
                            ? Schedule(due, msg, (due - Now).TotalSeconds) : throw new ArgumentException("at는 2026-01-31T15:10 같은 로컬 시각이어야 합니다.");
                    var mins = Num(args, "minutes_from_now") ?? throw new ArgumentException("at 또는 minutes_from_now가 필요합니다.");
                    return Schedule(Now.AddMinutes(mins), msg, mins * 60);
                case "list_timers":
                    var list = (pending ?? TimerService.Pending)().OrderBy(p => p.Due).ToList();
                    return list.Count == 0 ? "예약된 타이머가 없습니다." : string.Join("\n", list.Select(p => $"id={p.Id}: {p.Due.ToString("tt h:mm:ss", Korean)} '{p.Message}'"));
                case "cancel_timer":
                    var id = Str(args, "id") ?? throw new ArgumentException("id가 필요합니다.");
                    var target = (pending ?? TimerService.Pending)().FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("그 id의 타이머가 없습니다. list_timers로 확인하세요.");
                    (cancel ?? TimerService.Cancel)(id);
                    return $"'{target.Message}' 알림을 취소했습니다.";
                case "system_control":
                    return await ControlAsync(args, ct);
                default: return $"알 수 없는 도구: {name}";
            }
        }
        catch (ArgumentException ex) { return "인자 오류: " + ex.Message; }
        catch (Exception ex) { Log.Error("launcher tool failed: " + name, ex); return "실패했습니다: " + ex.Message; }
    }

    string Schedule(DateTime due, string message, double secs)
    {
        if (secs < 1 || secs > 7 * 86400) throw new ArgumentException("시간은 지금부터 1초 이상 7일 이하여야 합니다.");
        (schedule ?? TimerService.Schedule)(due, message);
        return $"{due.ToString("tt h:mm", Korean)}에 '{message}' 알림을 예약했습니다.";
    }

    async Task<string> ControlAsync(JsonObject args, CancellationToken ct)
    {
        var action = Str(args, "action") ?? throw new ArgumentException("action이 필요합니다.");
        var value = Num(args, "value");
        (string Id, string Done) c;
        switch (action)
        {
            case "volume_set":
                if (value is not (>= 0 and <= 100)) throw new ArgumentException("value는 0~100이어야 합니다.");
                c = ($"vol:{(int)value}", $"볼륨을 {(int)value}%로 맞췄습니다."); break;
            case "volume_change":
                if (value is not (>= -100 and <= 100) || (int)value == 0) throw new ArgumentException("value는 -100~100 (0 제외)이어야 합니다.");
                c = ($"vol:{(int)value:+0;-0}", $"볼륨을 {(int)value:+0;-0}% 조절했습니다."); break;
            case "mute": c = ("mute_on", "음소거했습니다."); break;
            case "unmute": c = ("unmute", "음소거를 해제했습니다."); break;
            case "dark_mode": c = ("dark", "다크 모드로 바꿨습니다."); break;
            case "light_mode": c = ("light", "라이트 모드로 바꿨습니다."); break;
            case "bluetooth_on": c = ("bt:on", "블루투스를 켰습니다."); break;
            case "wifi_on": c = ("wifi:on", "Wi-Fi를 켰습니다."); break;
            case "bluetooth_off" or "wifi_off":
                var radio = action == "wifi_off" ? "Wi-Fi" : "블루투스";
                if (confirm is null || !await confirm($"{radio} 끄기 — 연결이 끊어질 수 있습니다", ct)) return "사용자가 거절했습니다.";
                c = (action == "wifi_off" ? "wifi:off" : "bt:off", $"{radio}를 껐습니다."); break;
            default: throw new ArgumentException("알 수 없는 action: " + action);
        }
        (control ?? QuickControls.Run)(QuickControls.Prefix + c.Id);
        return c.Done;
    }

    public string Describe(string name, JsonObject args) => name switch
    {
        "cancel_timer" => $"타이머/알림 취소 (id {Str(args, "id")})",
        "system_control" => "시스템 조절: " + Str(args, "action"),
        _ => name,
    };

    static string? Str(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    static double? Num(JsonObject a, string k) => a[k] is JsonValue v ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<string>(out var s) && double.TryParse(s, CultureInfo.InvariantCulture, out var p) ? p : null : null;
}
