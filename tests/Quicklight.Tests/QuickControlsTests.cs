using Quicklight.Core.Shell;
using Xunit;

namespace Quicklight.Tests;

public class QuickControlsTests
{
    [Theory]
    [InlineData("볼륨 30", "ctl:vol:30")]
    [InlineData("volume +10", "ctl:vol:+10")]
    [InlineData("소리 -5", "ctl:vol:-5")]
    [InlineData("음소거", "ctl:mute")]
    [InlineData("unmute", "ctl:unmute")]
    [InlineData("다크 모드", "ctl:dark")]
    [InlineData("light mode", "ctl:light")]
    [InlineData("블루투스 끄기", "ctl:bt:off")]
    [InlineData("wifi on", "ctl:wifi:on")]
    public void Parses(string q, string id) => Assert.Equal(id, QuickControls.Parse(q)?.Id);

    [Theory]
    [InlineData("볼륨")]
    [InlineData("블루투스")]
    [InlineData("볼륨 300")]
    [InlineData("volume loud")]
    public void PlainQueriesStayOnSettings(string q) => Assert.Null(QuickControls.Parse(q));
}
