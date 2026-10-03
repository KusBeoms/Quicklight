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

public class PhoneticTests
{
    [Theory]
    [InlineData("애플뮤직", "Apple Music")]
    [InlineData("애플 뮤직", "Apple Music")]
    [InlineData("비주얼 스튜디오 코드", "Visual Studio Code")]
    [InlineData("디스코드", "Discord")]
    [InlineData("노션", "Notion")]
    [InlineData("유튜브", "YouTube")]
    [InlineData("깃허브", "GitHub")]
    [InlineData("넷플릭스", "Netflix")]
    [InlineData("팟플레이어", "PotPlayer")]
    [InlineData("melon", "멜론")]
    [InlineData("kakaotalk", "카카오톡")]
    public void Same_sounds_in_the_other_script(string query, string name) => Assert.Equal(74, FuzzyMatcher.Score(query, name));

    [Theory]
    [InlineData("뮤직", "Apple Music")] // a later word
    [InlineData("크롬", "Google Chrome")]
    [InlineData("애플", "Apple Music")] // the start
    public void Partial_sounds_match_lower(string query, string name) =>
        Assert.InRange(FuzzyMatcher.Score(query, name), 50, 70);

    [Theory]
    [InlineData("ㅋㄹㅁ", "Chrome", 80)]            // 크롬
    [InlineData("ㅋㄹ", "Chrome", 80)]              // the ㅁ is a final consonant, which initials do not show
    [InlineData("ㅋㄹ", "Google Chrome", 80)]       // a later word
    [InlineData("ㄷㅅㅋㄷ", "Discord", 80)]         // 디스코드
    [InlineData("ㅍㅌㅅ", "Photoshop", 80)]         // 포토샵
    [InlineData("ㅇㅍㅁㅈ", "Apple Music", 70)]     // 애플뮤직: two words, the ㄹ of 플 sits between ㅍ and ㅁ
    [InlineData("ㄱㅎㅂ", "GitHub", 70)]            // 깃허브: the ㅅ of 깃 sits between ㄱ and ㅎ
    [InlineData("ㅋㄹㅁ", "Chromecast", 60)]        // the start of a word only
    [InlineData("ㅋㄹㅁ", "Visual Chromecast", 54)]
    [InlineData("ㅋㄹ", "Claude", 0)]               // 클로드 is three syllables: ㅋㄹ is not all of it
    [InlineData("ㅋㄹ", "Calendar", 0)]
    [InlineData("ㅋㄹ", "Copilot", 0)]
    [InlineData("ㅋㄹ", "iCloud", 0)]
    [InlineData("ㅋㄹ", "Cloudflare WARP", 0)]
    [InlineData("ㅋㄹ", "Gyroflow", 0)]              // first and last sound only
    [InlineData("ㅋㄹ", "Google Gemini", 0)]         // 구글: ㄱㄱ, the ㅋ would be a repeat of the ㄱ
    [InlineData("ㅋㄹ", "Spreadsheet Compare", 0)]
    [InlineData("ㅋㄹ", "NVIDIA Control Panel", 0)]
    [InlineData("ㅋㄹ", "Global Flags (ARM)", 0)]
    [InlineData("ㅋㄹㅁ", "Google Gemini", 0)]
    [InlineData("ㅍㅌ", "Photoshop", 0)]            // two initials only count for a whole word
    [InlineData("ㅍㅌㅅ", "Chrome", 0)]
    [InlineData("ㅂㅈㅇ", "Notepad", 0)]
    public void Initial_consonants_find_names_in_the_other_script(string query, string name, double expected) =>
        Assert.Equal(expected, FuzzyMatcher.Score(query, name));

    [Theory]
    [InlineData("ㅋㄻ", "ㅋㄹㅁ")] // ㄹ+ㅁ
    [InlineData("ㄳ", "ㄱㅅ")]
    [InlineData("ㅄㅇ", "ㅂㅅㅇ")]
    [InlineData("ㅍㄿ", "ㅍㄹㅍ")]
    [InlineData("카카오톡", "카카오톡")] // syllables stay as they are
    [InlineData("chrome", "chrome")]
    public void The_IME_joins_consonants_typed_in_a_row(string typed, string meant) => Assert.Equal(meant, Hangul.SplitCompoundJamo(typed));

    [Fact]
    public void Joined_consonants_search_like_the_separate_ones()
    {
        Assert.Equal(FuzzyMatcher.Score("ㅋㄹㅁ", "Chrome"), FuzzyMatcher.Score("ㅋㄻ", "Chrome"));
        Assert.Equal(80, FuzzyMatcher.Score("ㅋㄻ", "Chrome"));
        Assert.Equal(FuzzyMatcher.Score("ㄱㅅ", "기사"), FuzzyMatcher.Score("ㄳ", "기사")); // 초성 of a Korean name
        Assert.True(FuzzyMatcher.Score("ㄳ", "기사") > 0);
    }

    [Theory]
    [InlineData("크롬", "Calculator")]
    [InlineData("애플", "Paint")]
    [InlineData("유튜브", "3D 뷰어")] // only the name's Latin letters are compared with a Korean query
    [InlineData("메모장", "Notepad")] // a translation, not a sound: found by the localized name instead
    public void Different_sounds_do_not_match(string query, string name) => Assert.Equal(0, FuzzyMatcher.Score(query, name));
}

public class TypoTests
{
    [Theory]
    [InlineData("fuison")]  // swapped letters
    [InlineData("fuision")] // an extra letter
    [InlineData("fuson")]   // a missing letter
    [InlineData("gusion")]  // g is next to f
    [InlineData("fuzion")]  // z is next to s
    public void Typos_still_find_the_word(string q) => Assert.InRange(FuzzyMatcher.Score(q, "Autodesk Fusion"), 50, 62);

    [Fact]
    public void A_neighbouring_key_is_likelier_than_a_far_one() =>
        Assert.True(FuzzyMatcher.Score("gusion", "Fusion") > FuzzyMatcher.Score("pusion", "Fusion"));

    [Fact]
    public void The_closest_name_wins() =>
        Assert.True(FuzzyMatcher.Score("gusion", "Fusion") > FuzzyMatcher.Score("gusion", "Vision"));

    [Theory]
    [InlineData("fus", "Fusion")]    // too short to guess
    [InlineData("zzzzzz", "Fusion")]
    public void No_guess(string q, string name) => Assert.True(FuzzyMatcher.Score(q, name) is 0 or >= 62);
}
