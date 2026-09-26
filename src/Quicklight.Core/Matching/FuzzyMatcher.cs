namespace Quicklight.Core.Matching;

/// <summary>
/// Scores how well a query matches a name, 0 (no match) to 100 (exact).
/// Tiers: exact > prefix > word-start/acronym > substring > choseong > ordered subsequence > sound in the other script.
/// </summary>
public static class FuzzyMatcher
{
    public static double Score(string query, string name)
    {
        var spelled = SpelledScore(query, name);
        if (spelled >= 62 || string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(name)) return spelled;
        // Below a clean match: maybe a typo ("gusion" → Fusion), then maybe the other script ("애플뮤직" → Apple Music).
        var typed = Math.Max(spelled, Typo.Score(query, name));
        return typed > 0 ? typed : Phonetic.Score(query.Trim(), name);
    }

    static double SpelledScore(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(name)) return 0;
        var q = query.Trim().ToLowerInvariant();
        var n = name.ToLowerInvariant();

        if (n == q) return 100;
        // Ignore a trailing extension so "notes" is an exact hit for "notes.txt".
        var dot = n.LastIndexOf('.');
        if (dot > 0 && n[..dot] == q) return 98;

        // Multi-word queries: every word must match somewhere; score is driven by the weakest word.
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            double min = 100, sum = 0;
            foreach (var w in words)
            {
                var s = ScoreSingle(w, n, name);
                if (s <= 0) return n.Replace(" ", "").Contains(q.Replace(" ", "")) ? 70 : 0;
                min = Math.Min(min, s); sum += s;
            }
            // In-order phrase gets a small bonus.
            double phrase = n.Contains(q) ? 8 : 0;
            return Math.Min(97, 0.6 * min + 0.4 * sum / words.Length + phrase - 4);
        }
        return ScoreSingle(q, n, name);
    }

    static double ScoreSingle(string q, string n, string original)
    {
        if (n == q) return 100;
        double lengthBonus = Math.Min(8, 8.0 * q.Length / Math.Max(n.Length, 1));

        if (n.StartsWith(q, StringComparison.Ordinal)) return 88 + lengthBonus;

        var starts = WordStarts(original);
        // Query is a prefix of some later word: "code" in "visual studio code".
        foreach (var s in starts)
            if (s > 0 && string.CompareOrdinal(n, s, q, 0, q.Length) == 0) return 78 + lengthBonus;

        // Acronym over word starts: "vsc" -> "Visual Studio Code".
        if (q.Length >= 2 && MatchesAcronym(q, n, starts)) return 74 + Math.Min(6, q.Length);

        int idx = n.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 62 + lengthBonus - Math.Min(6, idx * 0.3);

        if (Hangul.IsChoseongQuery(q))
        {
            var cho = Hangul.ToChoseong(original).Replace(" ", "");
            var qc = q.Replace(" ", "");
            if (cho.StartsWith(qc, StringComparison.Ordinal)) return 72 + Math.Min(8, qc.Length * 2);
            if (cho.Contains(qc, StringComparison.Ordinal)) return 58 + Math.Min(6, qc.Length);
        }

        // Two Hangul syllables already carry a lot of signal: "카톡" -> 카카오톡.
        if (q.Length >= 3 || (q.Length == 2 && Hangul.IsSyllable(q[0]) && Hangul.IsSyllable(q[1])))
        {
            var sub = SubsequenceScore(q, n, starts);
            if (sub > 0) return sub;
        }
        return 0;
    }

    /// <summary>Indices where a word starts: after space/punctuation, camelCase humps, letter/digit changes.</summary>
    internal static List<int> WordStarts(string s)
    {
        var list = new List<int>();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!char.IsLetterOrDigit(c)) continue;
            if (i == 0) { list.Add(i); continue; }
            char p = s[i - 1];
            if (!char.IsLetterOrDigit(p) || (char.IsUpper(c) && char.IsLower(p)) || (char.IsDigit(c) != char.IsDigit(p)))
                list.Add(i);
        }
        return list;
    }

    static bool MatchesAcronym(string q, string n, List<int> starts)
    {
        // Greedy: each query char consumes the next word start that begins with it,
        // allowing a few extra chars from the current word ("vscode" -> VS Code).
        int qi = 0;
        for (int w = 0; w < starts.Count && qi < q.Length; w++)
        {
            int pos = starts[w];
            if (n[pos] != q[qi]) continue;
            qi++;
            int end = w + 1 < starts.Count ? starts[w + 1] : n.Length;
            for (int k = pos + 1; k < end && qi < q.Length && n[k] == q[qi]; k++) qi++;
        }
        return qi == q.Length && starts.Count > 1;
    }

    static double SubsequenceScore(string q, string n, List<int> starts)
    {
        // Greedy matching from the leftmost q[0] can stretch the span over a stray early letter,
        // so try every occurrence of q[0] as the start and keep the best.
        double best = 0;
        for (int start = n.IndexOf(q[0]); start >= 0; start = n.IndexOf(q[0], start + 1))
            best = Math.Max(best, SubsequenceFrom(q, n, starts, start));
        return best;
    }

    static double SubsequenceFrom(string q, string n, List<int> starts, int start)
    {
        int qi = 0, first = -1, last = -1, boundaryHits = 0;
        for (int i = start; i < n.Length && qi < q.Length; i++)
        {
            if (n[i] != q[qi]) continue;
            if (first < 0) first = i;
            if (starts.Contains(i)) boundaryHits++;
            last = i; qi++;
        }
        if (qi < q.Length) return 0;
        double span = last - first + 1;
        double density = q.Length / span; // 1 = contiguous
        if (density < 0.4) return 0;
        double anchored = first == 0 ? 10 : 0; // starts where the name starts
        return 25 + 25 * density + Math.Min(12, boundaryHits * 4) + anchored;
    }
}
