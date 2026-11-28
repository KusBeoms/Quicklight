using Quicklight.Core.Ai;
using Xunit;

namespace Quicklight.Tests;

public class ClipboardActionsTests
{
    [Fact]
    public void EveryActionHasAPromptThatSaysOutputOnly()
    {
        Assert.Equal(6, ClipboardActions.All.Count);
        foreach (var a in ClipboardActions.All)
        {
            Assert.Contains("Output only", a.Task.System);
            Assert.Contains("<data>", a.Task.System);
            Assert.Contains(a.Task.Reminder, a.Task.User("x"));
        }
        Assert.Equal("영어로 번역", ClipboardActions.Find("en")!.Label);
        Assert.Null(ClipboardActions.Find("nope"));
    }

    [Fact]
    public void JsonPrettyPrintsWithoutModelAndKeepsHangul()
    {
        var pretty = ClipboardActions.Local("json", "{\"name\":\"퀵라이트\",\"n\":[1,2]}");
        Assert.NotNull(pretty);
        Assert.Contains("\n  \"name\": \"퀵라이트\"", pretty);
        Assert.Null(ClipboardActions.Local("json", "not json"));
        Assert.Null(ClipboardActions.Local("summary", "{}"));
    }

    [Fact]
    public void ClipCutsLongInput()
    {
        var (text, cut) = ClipboardActions.Clip(new string('a', ClipboardActions.MaxInput + 5));
        Assert.True(cut);
        Assert.Equal(ClipboardActions.MaxInput, text.Length);
        Assert.False(ClipboardActions.Clip("짧음").Truncated);
    }
}
