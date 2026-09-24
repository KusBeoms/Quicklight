using System.Text;

namespace Quicklight.Core.Matching;

/// <summary>
/// Hangul helpers: initial-consonant (choseong) extraction and 2-set (Dubeolsik) keyboard layout conversion,
/// so "zkzkdhxhr" typed with the IME off finds 카카오톡 and "ㅍㄴ챙ㄷ" typed with the IME on finds vscode.
/// </summary>
public static class Hangul
{
    const int SBase = 0xAC00, SCount = 11172, NCount = 588, TCount = 28;

    static readonly char[] Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ".ToCharArray();
    static readonly char[] Jung = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ".ToCharArray();
    // Index 0 = no final consonant.
    static readonly string[] Jong =
    [
        "", "ㄱ", "ㄲ", "ㄳ", "ㄴ", "ㄵ", "ㄶ", "ㄷ", "ㄹ", "ㄺ", "ㄻ", "ㄼ", "ㄽ", "ㄾ", "ㄿ", "ㅀ",
        "ㅁ", "ㅂ", "ㅄ", "ㅅ", "ㅆ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
    ];

    // Dubeolsik key -> compatibility jamo. Shift only changes ㅂㅈㄷㄱㅅㅐㅔ.
    static readonly Dictionary<char, char> KeyToJamo = new()
    {
        ['q'] = 'ㅂ', ['w'] = 'ㅈ', ['e'] = 'ㄷ', ['r'] = 'ㄱ', ['t'] = 'ㅅ', ['y'] = 'ㅛ', ['u'] = 'ㅕ', ['i'] = 'ㅑ', ['o'] = 'ㅐ', ['p'] = 'ㅔ',
        ['a'] = 'ㅁ', ['s'] = 'ㄴ', ['d'] = 'ㅇ', ['f'] = 'ㄹ', ['g'] = 'ㅎ', ['h'] = 'ㅗ', ['j'] = 'ㅓ', ['k'] = 'ㅏ', ['l'] = 'ㅣ',
        ['z'] = 'ㅋ', ['x'] = 'ㅌ', ['c'] = 'ㅊ', ['v'] = 'ㅍ', ['b'] = 'ㅠ', ['n'] = 'ㅜ', ['m'] = 'ㅡ',
        ['Q'] = 'ㅃ', ['W'] = 'ㅉ', ['E'] = 'ㄸ', ['R'] = 'ㄲ', ['T'] = 'ㅆ', ['O'] = 'ㅒ', ['P'] = 'ㅖ',
    };

    // Compound vowels and final consonants, as pairs of simple jamo.
    static readonly Dictionary<(char, char), char> VowelPairs = new()
    {
        [('ㅗ', 'ㅏ')] = 'ㅘ', [('ㅗ', 'ㅐ')] = 'ㅙ', [('ㅗ', 'ㅣ')] = 'ㅚ',
        [('ㅜ', 'ㅓ')] = 'ㅝ', [('ㅜ', 'ㅔ')] = 'ㅞ', [('ㅜ', 'ㅣ')] = 'ㅟ', [('ㅡ', 'ㅣ')] = 'ㅢ',
    };

    static readonly Dictionary<(char, char), char> JongPairs = new()
    {
        [('ㄱ', 'ㅅ')] = 'ㄳ', [('ㄴ', 'ㅈ')] = 'ㄵ', [('ㄴ', 'ㅎ')] = 'ㄶ', [('ㄹ', 'ㄱ')] = 'ㄺ', [('ㄹ', 'ㅁ')] = 'ㄻ',
        [('ㄹ', 'ㅂ')] = 'ㄼ', [('ㄹ', 'ㅅ')] = 'ㄽ', [('ㄹ', 'ㅌ')] = 'ㄾ', [('ㄹ', 'ㅍ')] = 'ㄿ', [('ㄹ', 'ㅎ')] = 'ㅀ', [('ㅂ', 'ㅅ')] = 'ㅄ',
    };

    // Must come after the pair tables: static fields initialize in declaration order.
    static readonly Dictionary<char, string> JamoToKeys = BuildJamoToKeys();

    static Dictionary<char, string> BuildJamoToKeys()
    {
        var map = new Dictionary<char, string>();
        foreach (var (k, j) in KeyToJamo) map[j] = k.ToString();
        foreach (var ((a, b), c) in VowelPairs) map[c] = map[a] + map[b];
        foreach (var ((a, b), c) in JongPairs) map[c] = map[a] + map[b];
        return map;
    }

    public static bool IsSyllable(char c) => c >= SBase && c < SBase + SCount;
    public static bool IsJamo(char c) => c >= 0x3131 && c <= 0x3163;
    public static bool IsConsonantJamo(char c) => c >= 0x3131 && c <= 0x314E;
    public static bool ContainsHangul(string s) => s.Any(c => IsSyllable(c) || IsJamo(c));

    /// <summary>"카카오톡" -> "ㅋㅋㅇㅌ". Non-Hangul characters are kept as they are (lower-cased).</summary>
    public static string ToChoseong(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(IsSyllable(c) ? Cho[(c - SBase) / NCount] : char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>True when the query is only consonant jamo, e.g. "ㅋㅋㅇㅌ".</summary>
    public static bool IsChoseongQuery(string q) => q.Length > 0 && q.All(c => IsConsonantJamo(c) || c == ' ');

    /// <summary>Hangul text -> the QWERTY keys that type it. "ㅍㄴ챙ㄷ" -> "vscode". Returns null if nothing changed.</summary>
    public static string? HangulToQwerty(string s)
    {
        if (!ContainsHangul(s)) return null;
        var sb = new StringBuilder(s.Length * 2);
        foreach (var c in s)
        {
            if (IsSyllable(c))
            {
                int i = c - SBase;
                sb.Append(JamoToKeys[Cho[i / NCount]]);
                sb.Append(JamoToKeys[Jung[i % NCount / TCount]]);
                var jong = Jong[i % TCount];
                if (jong.Length > 0) sb.Append(JamoToKeys[jong[0]]);
            }
            else if (JamoToKeys.TryGetValue(c, out var keys)) sb.Append(keys);
            else sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>QWERTY keys -> composed Hangul, as the IME would have produced. "zkzkdhxhr" -> "카카오톡". Returns null if the input has no letters.</summary>
    public static string? QwertyToHangul(string s)
    {
        if (!s.Any(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) || ContainsHangul(s)) return null;
        // All caps is Caps Lock, not Shift: "ZKZKDHXHR" is still 카카오톡, not 카카오톢.
        if (!s.Any(char.IsLower)) s = s.ToLowerInvariant();
        var jamo = new List<char>(s.Length);
        foreach (var c in s)
        {
            if (KeyToJamo.TryGetValue(c, out var j)) jamo.Add(j);
            else if (KeyToJamo.TryGetValue(char.ToLowerInvariant(c), out j)) jamo.Add(j);
            else jamo.Add(c);
        }
        return Compose(jamo);
    }

    static bool IsVowel(char c) => c >= 'ㅏ' && c <= 'ㅣ';
    static int ChoIndex(char c) => Array.IndexOf(Cho, c);
    static int JongIndex(char c) => Array.IndexOf(Jong, c.ToString());

    /// <summary>Standard Dubeolsik automaton over a jamo stream.</summary>
    static string Compose(List<char> jamo)
    {
        var sb = new StringBuilder();
        int n = jamo.Count, i = 0;
        while (i < n)
        {
            char c = jamo[i];
            if (!IsJamo(c)) { sb.Append(c); i++; continue; }

            // A lone vowel (possibly compound) with no initial consonant.
            if (IsVowel(c))
            {
                if (i + 1 < n && VowelPairs.TryGetValue((c, jamo[i + 1]), out var cv)) { sb.Append(cv); i += 2; }
                else { sb.Append(c); i++; }
                continue;
            }

            int cho = ChoIndex(c);
            if (cho < 0 || i + 1 >= n || !IsVowel(jamo[i + 1]))
            {
                sb.Append(c); i++;
                continue;
            }

            char v = jamo[i + 1];
            int j = i + 2;
            if (j < n && VowelPairs.TryGetValue((v, jamo[j]), out var compound)) { v = compound; j++; }
            int jung = Array.IndexOf(Jung, v);

            // Final consonant: only if the consonant is not the start of the next syllable.
            int jong = 0;
            if (j < n && IsJamo(jamo[j]) && !IsVowel(jamo[j]) && JongIndex(jamo[j]) > 0)
            {
                bool nextStartsSyllable = j + 1 < n && IsVowel(jamo[j + 1]);
                if (!nextStartsSyllable)
                {
                    char f = jamo[j];
                    if (j + 1 < n && JongPairs.TryGetValue((f, jamo[j + 1]), out var pair) && !(j + 2 < n && IsVowel(jamo[j + 2])))
                    {
                        jong = JongIndex(pair); j += 2;
                    }
                    else { jong = JongIndex(f); j++; }
                }
            }

            sb.Append((char)(SBase + cho * NCount + jung * TCount + jong));
            i = j;
        }
        return sb.ToString();
    }
}
