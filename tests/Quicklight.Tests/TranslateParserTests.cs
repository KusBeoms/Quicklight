using Quicklight.Core.Ai;
using Xunit;

namespace Quicklight.Tests;

public class TranslateParserTests
{
    [Theory]
    [InlineData("번역 안녕하세요", "안녕하세요", "English")]
    [InlineData("tr hello", "hello", "Korean")]
    [InlineData("안녕하세요 영어로", "안녕하세요", "English")]
    [InlineData("hello 일본어로", "hello", "Japanese")]
    [InlineData("translate good morning to japanese", "good morning", "Japanese")]
    [InlineData("번역 hello 중국어로", "hello", "Chinese")]
    public void Parses(string q, string text, string lang)
    {
        var r = TranslateParser.Parse(q);
        Assert.NotNull(r);
        Assert.Equal(text, r!.Text);
        Assert.Equal(lang, r.Language);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("chrome 영어")]
    [InlineData("번역")]
    public void Ignores(string q) => Assert.Null(TranslateParser.Parse(q));
}
