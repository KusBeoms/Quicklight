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

    const string Choseong = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";

    // Initial consonants ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ; ㅇ is silent at the start.
    static readonly string[] Initial = ["K", "K", "N", "T", "T", "L", "M", "P", "P", "S", "S", "", "S", "S", "S", "K", "T", "P", "H"];
    // Final consonants (none)ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ: ㅅ ㅈ ㅊ ㅎ sound like ㄷ, ㅇ is "ng".
    static readonly string[] Final = ["", "K", "K", "K", "N", "N", "N", "T", "L", "K", "M", "L", "L", "L", "P", "L", "M", "P", "P", "T", "T", "NK", "T", "T", "K", "T", "P", "T"];

    /// <summary>0 or a match score below the spelled tiers: 74 same sounds, 64 the name starts so, 58 a later word does. Initial consonants: see <see cref="ChoseongScore"/>.</summary>
    public static double Score(string query, string name)
    {
        bool hangulQuery = IsHangul(query), latinQuery = IsLatin(query);
        if (!(hangulQuery && HasLatin(name) || latinQuery && HasHangul(name))) return 0;
        // Only the name's letters in the other script count: "3D 뷰어" is D against a Korean query, not 뷰어.
        var q = Sounds(query);
        if (q.Length < 2) return 0;
        if (hangulQuery && query.Any(Hangul.IsConsonantJamo)) return ChoseongScore(q, name);
        var n = Sounds(name, latin: hangulQuery, hangul: latinQuery);
        if (n == q) return 74;
        if (q.Length < 3) return n.StartsWith(q, StringComparison.Ordinal) ? 55 : 0; // "애플": two sounds match a lot, so low
        if (n.StartsWith(q, StringComparison.Ordinal)) return 64;
        foreach (var start in FuzzyMatcher.WordStarts(name))
            if (start > 0 && Sounds(name[start..], hangulQuery, latinQuery).StartsWith(q, StringComparison.Ordinal)) return 58;
        return 0;
    }

    /// <summary>
    /// Initial consonants ("ㅋㄹ" for Chrome = 크롬) against the Latin words of a name. Two initials fit almost any name
    /// ("Claude", "Calendar", "Copilot" all start with K and L), so a query must account for whole words: the initials
    /// are the name's consonant sounds from the start of a word (or several words in a row) to its end, giving the
    /// vowels and final consonants they do not show. 80 for that, 70 when a final consonant sits between two initials
    /// ("애플뮤직": the ㄹ of 플); three or more initials that only start a word get 60 (54 for a later word), with nothing
    /// missing between them.
    /// </summary>
    static double ChoseongScore(string q, string name)
    {
        var starts = FuzzyMatcher.WordStarts(name);
        var words = new string[starts.Count];
        for (int i = 0; i < words.Length; i++)
            words[i] = Sounds(name[starts[i]..(i + 1 < words.Length ? starts[i + 1] : name.Length)], latin: true, hangul: false);
        double best = 0;
        for (int a = 0; a < words.Length; a++)
        {
            var n = "";
            var firsts = new List<int>(); // where each later word's first sound sits in n
            for (int b = a; b < words.Length; b++)
            {
                if (n.Length > 0 && words[b].Length > 0) firsts.Add(n.Length);
                int skipped = Covers(q, n += words[b], firsts);
                if (skipped == 0) return 80;
                if (skipped > 0) best = 70;
            }
        }
        if (best > 0 || q.Length < 3) return best;
        for (int a = 0; a < words.Length; a++)
            if (words[a].StartsWith(q, StringComparison.Ordinal)) return a == 0 ? 60 : 54;
        return 0;
    }

    /// <summary>Sounds a name may have between two initial consonants: one, the final consonant of a syllable.</summary>
    const int MaxSkipped = 1;

    /// <summary>
    /// How many sounds were skipped when the sounds of <paramref name="q"/> make up <paramref name="n"/> (fewest over the
    /// ways it fits), or -1 when they do not. They are in order from n's first sound, with nothing or one sound left at
    /// the end (a final consonant the initials do not show). With two initials that one may only be M, N or L: other
    /// sounds get a vowel and a syllable of their own in Korean ("클로드" for Claude, but "크롬" for Chrome). A skipped
    /// sound is a final consonant, so it is never the same sound as the initial before or after it (that would be "Google"
    /// for ㅋㄹ), and a word's first sound is never skipped (<paramref name="firsts"/> says where they are).
    /// </summary>
    static int Covers(string q, string n, List<int> firsts)
    {
        if (n.Length == 0 || n[0] != q[0]) return -1;
        var at = new Dictionary<int, int> { [0] = 0 }; // where the sounds so far can end, with the fewest skipped: the earliest match is not always the one that lets the rest fit
        for (int i = 1; i < q.Length; i++)
        {
            var next = new Dictionary<int, int>();
            foreach (var (p, skips) in at)
                for (int k = p + 1; k <= p + 1 + MaxSkipped && k < n.Length; k++)
                {
                    int skipped = k - p - 1;
                    if (n[k] == q[i] && (skipped == 0 || n[p + 1] != n[p] && n[p + 1] != n[k]))
                        next[k] = Math.Min(next.GetValueOrDefault(k, int.MaxValue), skips + skipped);
                    if (firsts.Contains(k)) break; // the next sound would have to skip this word's first
                }
            if (next.Count == 0) return -1;
            at = next;
        }
        int best = -1;
        foreach (var (p, skips) in at)
        {
            int left = n.Length - 1 - p;
            if ((left == 0 || left == 1 && (q.Length >= 3 || n[^1] is 'M' or 'N' or 'L')) && (best < 0 || skips < best)) best = skips;
        }
        return best;
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
        var s = SplitHumps(text);
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
            if (Hangul.IsConsonantJamo(c))
            {
                // A bare initial consonant stands for a syllable that starts with it.
                int initial = hangul ? Choseong.IndexOf(c) : -1;
                if (initial >= 0) { Add(Initial[initial]); vowelSince = true; }
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

    /// <summary>Lower case, with a space at each camelCase hump ("GitHub" → "git hub"). Runs for most names on every keystroke.</summary>
    static string SplitHumps(string text)
    {
        static bool Hump(string t, int i) => i > 0 && t[i] is >= 'A' and <= 'Z' && t[i - 1] is >= 'a' and <= 'z';
        int humps = 0;
        for (int i = 1; i < text.Length; i++) if (Hump(text, i)) humps++;
        if (humps == 0) return text.ToLowerInvariant();
        return string.Create(text.Length + humps, text, (span, t) =>
        {
            int j = 0;
            for (int i = 0; i < t.Length; i++)
            {
                if (Hump(t, i)) span[j++] = ' ';
                span[j++] = char.ToLowerInvariant(t[i]);
            }
        });
    }

    static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';

    static bool IsHangul(string s) => s.Any(IsHangulLetter) && s.All(c => IsHangulLetter(c) || c == ' ');
    static bool IsHangulLetter(char c) => Hangul.IsSyllable(c) || Hangul.IsConsonantJamo(c);
    static bool IsLatin(string s) => s.Any(char.IsAsciiLetter) && s.All(c => char.IsAsciiLetter(c) || c == ' ');
    static bool HasLatin(string s) => s.Any(char.IsAsciiLetter);
    static bool HasHangul(string s) => s.Any(Hangul.IsSyllable);
}
