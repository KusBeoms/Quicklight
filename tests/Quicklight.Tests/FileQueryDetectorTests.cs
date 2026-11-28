using Quicklight.Core.Ai;
using Xunit;

namespace Quicklight.Tests;

public class FileQueryDetectorTests
{
    [Theory]
    [InlineData("지난주에 받은 견적서 pdf")]
    [InlineData("어제 만든 엑셀")]
    [InlineData("작년 여름 제주도 사진")]
    [InlineData("3월에 고친 발표 자료")]
    public void Detects(string q) => Assert.True(FileQueryDetector.IsFileQuery(q));

    [Theory]
    [InlineData("chrome")]
    [InlineData("견적서")]
    [InlineData("어제")]
    [InlineData("visual studio code")]
    public void Ignores(string q) => Assert.False(FileQueryDetector.IsFileQuery(q));

    [Fact]
    public void CleansAnswer()
    {
        Assert.Equal("dm:lastweek ext:pdf 견적서", FileQueryDetector.CleanQuery("```\ndm:lastweek ext:pdf 견적서\n```"));
        Assert.Equal("ext:pdf", FileQueryDetector.CleanQuery("\"ext:pdf\""));
        Assert.Null(FileQueryDetector.CleanQuery("a\nb"));
    }
}
