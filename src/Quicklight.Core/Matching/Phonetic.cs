using System.Text;

namespace Quicklight.Core.Matching;

/// <summary>
/// Finds a name written in the other script by how it sounds: "애플뮤직" → Apple Music, "크롬" → Chrome, "melon" → 멜론.
/// Both sides are reduced to their consonant sounds (P T K S L M N H: 애플뮤직 = ㅍㄹㅁㅈㄱ = PLMSK = apple music), vowels
/// dropped, and compared.
/// </summary>
public static class Phonetic
{
    // ponytail: a sound sketch, not real transliteration. English spelling is loose ("edge" 엣지, "kakaotalk"'s silent l
    // miss); a loanword dictionary is the upgrade if these matter.

    // Initial consonants ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ; ㅇ is silent at the start.
    static readonly string[] Initial = ["K", "K", "N", "T", "T", "L", "M", "P", "P", "S", "S", "", "S", "S", "S", "K", "T", "P", "H"];
    // Final consonants (none)ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ: ㅅ ㅈ ㅊ ㅎ sound like ㄷ, ㅇ is "ng".
    static readonly string[] Final = ["", "K", "K", "K", "N", "N", "N", "T", "L", "K", "M", "L", "L", "L", "P", "L", "M", "P", "P", "T", "T", "NK", "T", "T", "K", "T", "P", "T"];

    /// <summary>0 or a match score below the spelled tiers: 74 same sounds, 64 the name starts so, 58 a later word does.</summary>
    public static double Score(string query, string name)
    {
        bool hangulQuery = IsHangul(query), latinQuery = IsLatin(query);
        if (!(hangulQuery && HasLatin(name) || latinQuery && HasHangul(name))) return 0;
        // Only the name's letters in the other script count: "3D 뷰어" is D against a Korean query, not 뷰어.
        var q = Sounds(query);
        if (q.Length < 2) return 0;
        var n = Sounds(name, latin: hangulQuery, hangul: latinQuery);
        if (n == q) return 74;
        if (q.Length < 3) return n.StartsWith(q, StringComparison.Ordinal) ? 55 : 0; // "애플": two sounds match a lot, so low
        if (n.StartsWith(q, StringComparison.Ordinal)) return 64;
        foreach (var start in FuzzyMatcher.WordStarts(name))
            if (start > 0 && Sounds(name[start..], hangulQuery, latinQuery).StartsWith(q, StringComparison.Ordinal)) return 58;
        return 0;
    }

    /// <summary>The consonant sounds of Hangul syllables and Latin letters; anything else is skipped.</summary>
    internal static string Sounds(string text, bool latin = true, bool hangul = true)
    {
        var sb = new StringBuilder();
        bool vowelSince = true; // the same sound twice in a row is one ("apple": pp, "멜론": ㄹㄹ), not across a vowel
        void Add(string sounds)
        {
            foreach (var c in sounds)
            {
                if (!vowelSince && sb.Length > 0 && sb[^1] == c) continue;
                sb.Append(c);
                vowelSince = false;
            }
        }

        // Letter pairs never span a camelCase hump: "GitHub" is git-hub (깃허브), not gi-thub.
        var s = System.Text.RegularExpressions.Regex.Replace(text, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (Hangul.IsSyllable(c))
            {
                if (!hangul) continue;
                int code = c - 0xAC00;
                Add(Initial[code / 588]);
                vowelSince = true;
                Add(Final[code % 28]);
                continue;
            }
            if (c is < 'a' or > 'z' || !latin) continue;
            char next = i + 1 < s.Length ? s[i + 1] : '\0', after = i + 2 < s.Length ? s[i + 2] : '\0';
            switch (c)
            {
                case 'a' or 'e' or 'i' or 'o' or 'u' or 'y' or 'w': vowelSince = true; break;
                case 'p' when next == 'h': Add("P"); i++; break;             // photoshop
                case 'b' or 'p' or 'f' or 'v': Add("P"); break;
                case 't' when next == 'h': Add("T"); i++; break;
                case 't' when next == 'i' && IsVowel(after): Add("S"); break; // notion 노션
                case 'd' or 't': Add("T"); break;
                case 'c' when next == 'h': Add(after == 'r' ? "K" : "S"); i++; break; // chrome 크롬, chat 챗
                case 'c' when next is 'e' or 'i' or 'y': Add("S"); break;
                case 'k' or 'q' or 'c': Add("K"); break;
                case 'g' when next == 'h': i++; break;                        // night: silent
                case 'g': Add("K"); break;
                case 's' when next == 'h': Add("S"); i++; break;
                case 's' or 'z' or 'j': Add("S"); break;
                case 'x': Add("KS"); break;
                case 'r': if (IsVowel(next)) Add("L"); break;                 // "discord" 디스코드: r before a consonant is not said
                case 'l' when next is 'k' or 'm' && i > 0 && s[i - 1] == 'a': break; // talk 톡, calm 캄
                case 'l': Add("L"); break;
                case 'm': Add("M"); break;
                case 'n': Add("N"); break;
                case 'h': if (IsVowel(next)) Add("H"); break;
            }
        }
        return sb.ToString();
    }

    static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';

    static bool IsHangul(string s) => s.Any(Hangul.IsSyllable) && s.All(c => Hangul.IsSyllable(c) || c == ' ');
    static bool IsLatin(string s) => s.Any(char.IsAsciiLetter) && s.All(c => char.IsAsciiLetter(c) || c == ' ');
    static bool HasLatin(string s) => s.Any(char.IsAsciiLetter);
    static bool HasHangul(string s) => s.Any(Hangul.IsSyllable);
}
