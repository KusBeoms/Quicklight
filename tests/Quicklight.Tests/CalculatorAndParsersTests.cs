using Quicklight.Core.Calc;
using Quicklight.Core.Providers;

namespace Quicklight.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("2 * (3 + 4)", 14)]
    [InlineData("2(3+4)", 14)]
    [InlineData("-3^2", -9)]
    [InlineData("2^3^2", 512)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("10 % 3", 1)]
    [InlineData("50%", 0.5)]
    [InlineData("1,000 * 3", 3000)]
    [InlineData("3 × 4 ÷ 2", 6)]
    [InlineData("sqrt(16) + abs(-2)", 6)]
    [InlineData("2pi", 2 * Math.PI)]
    [InlineData("log(1000)", 3)]
    [InlineData("1.5e3 + 1", 1501)]
    [InlineData("12*12=", 144)]
    public void Evaluates(string expr, double expected)
    {
        Assert.True(Calculator.TryEvaluate(expr, out var v), expr);
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-5")]
    [InlineData("chrome")]
    [InlineData("windows 11")]
    [InlineData("2024-01-15")]
    [InlineData("img-001")]
    [InlineData("1/0")]
    [InlineData("(1+2")]
    [InlineData("c++")]
    [InlineData("010-1234-5678")]   // phone number
    [InlineData("2020-2024")]       // year range
    public void Rejects_non_math(string text) => Assert.False(Calculator.TryEvaluate(text, out _), text);

    [Fact]
    public void Deep_nesting_is_rejected_not_a_stack_overflow()
    {
        Assert.False(Calculator.TryEvaluate(new string('(', 100_000) + "1", out _));
        Assert.False(Calculator.TryEvaluate("1+" + new string('-', 100_000) + "1", out _));
        Assert.True(Calculator.TryEvaluate(new string('(', 50) + "1+1" + new string(')', 50), out var v));
        Assert.Equal(2, v);
    }

    [Fact]
    public void No_negative_zero()
    {
        Assert.True(Calculator.TryEvaluate("0*-1", out var v));
        Assert.Equal("0", Calculator.Format(v));
        Assert.Equal("0", Calculator.FormatPlain(v));
    }

    [Fact]
    public void Formats()
    {
        Assert.Equal("1,234,567", Calculator.Format(1234567));
        Assert.Equal("1234567", Calculator.FormatPlain(1234567));
        Assert.Equal("0.3", Calculator.Format(0.1 + 0.2));
    }
}

public class UrlAndPathTests
{
    [Theory]
    [InlineData("https://github.com/x", "https://github.com/x", true)]
    [InlineData("www.naver.com", "https://www.naver.com", true)]
    [InlineData("naver.com", "https://naver.com", false)]
    [InlineData("localhost:3000/api", "http://localhost:3000/api", true)]
    [InlineData("192.168.0.1", "http://192.168.0.1", true)]
    public void Detects_urls(string text, string url, bool isExplicit)
    {
        var p = UrlProvider.Parse(text);
        Assert.NotNull(p);
        Assert.Equal(url, p!.Value.Url);
        Assert.Equal(isExplicit, p.Value.Explicit);
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData("hello world.com")]
    [InlineData("chrome")]
    [InlineData("readme.md")]
    public void Ignores_non_urls(string text) => Assert.Null(UrlProvider.Parse(text));

    [Fact]
    public void Expands_paths()
    {
        Assert.Equal(@"C:\", PathProvider.Expand("c:"));
        Assert.Equal(@"C:\Windows\System32", PathProvider.Expand("C:/Windows/System32"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), PathProvider.Expand("%APPDATA%"));
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), PathProvider.Expand(@"~\Downloads"));
        Assert.Null(PathProvider.Expand("chrome"));
    }
}
