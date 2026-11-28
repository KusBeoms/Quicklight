using System.Text.Json.Nodes;
using Quicklight.Core.Ai;
using Quicklight.Core.Providers;
using Xunit;

namespace Quicklight.Tests;

public class LauncherToolsTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 15, 0, 0);

    static (LauncherTools Tools, List<(DateTime, string)> Sched, List<string> Ctl, List<string> Cancelled) Make(bool allow = true)
    {
        List<(DateTime, string)> sched = []; List<string> ctl = [], cancelled = [];
        var tools = new LauncherTools((_, _) => Task.FromResult(allow), () => T0, (d, m) => sched.Add((d, m)),
            () => [new PendingTimer("abc", T0.AddMinutes(5), "빨래")], cancelled.Add, ctl.Add);
        return (tools, sched, ctl, cancelled);
    }

    static Task<string> Call(LauncherTools t, string name, string json) => t.CallAsync(name, JsonNode.Parse(json)!.AsObject(), default);

    [Fact]
    public async Task TimerAndReminderSchedule()
    {
        var (t, sched, _, _) = Make();
        Assert.Contains("예약", await Call(t, "set_timer", """{"minutes":10,"label":"빨래"}"""));
        Assert.Contains("예약", await Call(t, "set_reminder", """{"at":"2026-01-01T16:30","message":"회의"}"""));
        Assert.Equal([(T0.AddMinutes(10), "빨래"), (new DateTime(2026, 1, 1, 16, 30, 0), "회의")], sched);
        Assert.StartsWith("인자 오류", await Call(t, "set_timer", "{}"));
        Assert.StartsWith("인자 오류", await Call(t, "set_reminder", """{"at":"2025-01-01T10:00","message":"x"}"""));
        Assert.Equal(2, sched.Count);
    }

    [Fact]
    public async Task CancelNeedsKnownId()
    {
        var (t, _, _, cancelled) = Make();
        Assert.StartsWith("인자 오류", await Call(t, "cancel_timer", """{"id":"zzz"}"""));
        Assert.Contains("취소", await Call(t, "cancel_timer", """{"id":"abc"}"""));
        Assert.Equal(["abc"], cancelled);
        Assert.False(t.Tools.Single(x => x.Name == "cancel_timer").ReadOnly);
    }

    [Fact]
    public async Task SystemControlDispatchAndConsent()
    {
        var (t, _, ctl, _) = Make();
        await Call(t, "system_control", """{"action":"volume_set","value":20}""");
        await Call(t, "system_control", """{"action":"volume_change","value":-10}""");
        await Call(t, "system_control", """{"action":"dark_mode"}""");
        await Call(t, "system_control", """{"action":"bluetooth_off"}""");
        Assert.Equal(["ctl:vol:20", "ctl:vol:-10", "ctl:dark", "ctl:bt:off"], ctl);
        Assert.StartsWith("인자 오류", await Call(t, "system_control", """{"action":"volume_set","value":150}"""));

        var (denied, _, ctl2, _) = Make(allow: false);
        Assert.Contains("거절", await Call(denied, "system_control", """{"action":"wifi_off"}"""));
        await Call(denied, "system_control", """{"action":"wifi_on"}""");
        Assert.Equal(["ctl:wifi:on"], ctl2);
    }
}
