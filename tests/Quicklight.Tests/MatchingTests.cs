using Quicklight.Core.Matching;

namespace Quicklight.Tests;

public class HangulTests
{
    [Theory]
    [InlineData("zkzkdhxhr", "카카오톡")]
    [InlineData("gksrmf", "한글")]
    [InlineData("dkssudgktpdy", "안녕하세요")]
    [InlineData("rhkdrhwl", "광고지")]
    [InlineData("dlfrl", "일기")]
    [InlineData("dlfrrl", "읽기")]
    [InlineData("qkfkaRhc", "바람꽃")]
    [InlineData("rkqt", "값")]
    [InlineData("dksw", "앉")]
    [InlineData("dkswk", "안자")]
    [InlineData("dnjsem", "원드")]
    [InlineData("akdlzmfhthvmxm", "마이크로소프트")]
    [InlineData("ZKZKDHXHR", "카카오톡")]   // Caps Lock on
    public void QwertyToHangul_composes_like_the_ime(string keys, string expected) =>
        Assert.Equal(expected, Hangul.QwertyToHangul(keys));

    [Theory]
    [InlineData("ㅍㄴ챙ㄷ", "vscode")]
    [InlineData("초개ㅡㄷ", "chrome")]
    [InlineData("카카오톡", "zkzkdhxhr")]
    [InlineData("ㅜㅐㅅㄷㅔㅁㅇ", "notepad")]
    public void HangulToQwerty_recovers_keys(string hangul, string expected) =>
        Assert.Equal(expected, Hangul.HangulToQwerty(hangul));

    [Fact]
    public void Conversions_return_null_when_not_applicable()
    {
        Assert.Null(Hangul.QwertyToHangul("123"));
        Assert.Null(Hangul.QwertyToHangul("한글"));
        Assert.Null(Hangul.HangulToQwerty("code"));
    }

    [Fact]
    public void Choseong()
    {
        Assert.Equal("ㅋㅋㅇㅌ", Hangul.ToChoseong("카카오톡"));
        Assert.Equal("ㅂㅈㅇ vs", Hangul.ToChoseong("비주얼 VS"));
        Assert.True(Hangul.IsChoseongQuery("ㅋㅋㅇㅌ"));
        Assert.False(Hangul.IsChoseongQuery("카톡"));
    }
}

public class FuzzyMatcherTests
{
    [Fact]
    public void Tiers_are_ordered()
    {
        double exact = FuzzyMatcher.Score("chrome", "Chrome");
        double prefix = FuzzyMatcher.Score("chr", "Chrome");
        double word = FuzzyMatcher.Score("code", "Visual Studio Code");
        double acronym = FuzzyMatcher.Score("vsc", "Visual Studio Code");
        double substring = FuzzyMatcher.Score("rom", "Chrome");
        double subsequence = FuzzyMatcher.Score("crm", "Chrome");
        Assert.Equal(100, exact);
        Assert.True(prefix > word, $"{prefix} > {word}");
        Assert.True(word > acronym, $"{word} > {acronym}");
        Assert.True(acronym > substring, $"{acronym} > {substring}");
        Assert.True(substring > subsequence, $"{substring} > {subsequence}");
        Assert.True(subsequence > 0);
    }

    [Theory]
    [InlineData("vscode", "Visual Studio Code")]
    [InlineData("ㅋㅋㅇㅌ", "카카오톡")]
    [InlineData("카톡", "카카오톡")]
    [InlineData("studio code", "Visual Studio Code")]
    [InlineData("notes", "notes.txt")]
    [InlineData("ps", "PowerShell")]
    public void Matches(string q, string name) => Assert.True(FuzzyMatcher.Score(q, name) >= 50, $"{q} vs {name}: {FuzzyMatcher.Score(q, name)}");

    [Theory]
    [InlineData("xyz", "Chrome")]
    [InlineData("zzz", "Visual Studio Code")]
    [InlineData("code chrome", "Visual Studio Code")]
    public void Rejects(string q, string name) => Assert.Equal(0, FuzzyMatcher.Score(q, name));

    [Fact]
    public void Subsequence_is_not_fooled_by_an_early_stray_letter() =>
        // Greedy leftmost matching would start at the first 'm' and stretch over the whole name.
        Assert.True(FuzzyMatcher.Score("mgr", "my long app - manager") > 0);

    [Fact]
    public void Shorter_names_win_for_the_same_prefix() =>
        Assert.True(FuzzyMatcher.Score("note", "Notepad") > FuzzyMatcher.Score("note", "Notepad++ Plugin Admin Tool"));
}
