using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Xunit;

namespace Quicklight.Tests;

public class TimerTests
{
    static readonly DateTime Now = new(2026, 1, 5, 14, 0, 0); // 오후 2시

    static string? Target(string q) => TimerProvider.Evaluate(q, Now, []).FirstOrDefault()?.Target;

    [Theory]
    [InlineData("5분", 300), InlineData("90초", 90), InlineData("1시간 30분", 5400), InlineData("1시간반", 5400),
     InlineData("25:00", 1500), InlineData("1:30:00", 5400), InlineData("5min", 300), InlineData("10 sec", 10), InlineData("2h", 7200),
     InlineData("타이머 5분", 300), InlineData("timer 2h", 7200)]
    public void Durations(string q, int secs) => Assert.Equal($"in|{secs}|타이머", Target(q));

    [Theory]
    [InlineData("5m"), InlineData("5s"), InlineData("5"), InlineData("5 km"), InlineData("3시간 회의")]
    public void NotATimer(string q) => Assert.Empty(TimerProvider.Evaluate(q, Now, []));

    [Fact]
    public void RelativeReminder() => Assert.Equal("in|600|회의", Target("10분 뒤 회의"));

    [Theory]
    [InlineData("3시 회의", 15, 0), InlineData("오전 3시 회의", 27, 0), InlineData("오후 3:30 회의", 15, 30), InlineData("3pm 회의", 15, 0),
     InlineData("내일 9시 회의", 33, 0), InlineData("1시 반 회의", 25, 30)]
    public void AbsoluteReminder(string q, int hour, int minute)
    {
        var due = new DateTime(long.Parse(Target(q)!.Split('|')[1]));
        Assert.Equal(Now.Date.AddHours(hour).AddMinutes(minute), due);
    }

    [Fact]
    public void RanksBelowFullNameAppMatch()
    {
        var r = TimerProvider.Evaluate("5분", Now, [])[0];
        Assert.True(r.Score < Scores.AppBase + 100 && r.Score < Scores.Date);
        Assert.Equal(ActionType.Timer, r.Action);
    }

    [Fact]
    public void ListsPendingAndClock()
    {
        var rows = TimerProvider.Evaluate("타이머", Now, [new("abc", Now.AddMinutes(5), "빨래")]);
        Assert.Equal("cancel|abc", rows[0].Target);
        Assert.Equal("ms-clock:", rows[^1].Target);
    }
}
