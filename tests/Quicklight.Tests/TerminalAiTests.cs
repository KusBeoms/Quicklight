using Quicklight.Core.Ai;
using Xunit;

namespace Quicklight.Tests;

public class TerminalAiTests
{
    [Theory]
    [InlineData("```cmd\ndir /b\n```", "dir /b")]
    [InlineData("$ ipconfig\nthen more", "ipconfig")]
    [InlineData("`> echo hi`", "echo hi")]
    public void CleanCommand_StripsDecoration(string answer, string expected) => Assert.Equal(expected, TerminalAi.CleanCommand(answer));

    [Fact]
    public void ExplainUser_KeepsTail()
    {
        var output = string.Join('\n', Enumerable.Range(1, 100).Select(i => "line" + i));
        var text = TerminalAi.ExplainUser("x", output);
        Assert.Contains("line100", text);
        Assert.DoesNotContain("line40\n", text);
    }
}
