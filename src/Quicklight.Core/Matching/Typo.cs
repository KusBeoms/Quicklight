namespace Quicklight.Core.Matching;

/// <summary>
/// Finds a name through a typo: "fuison", "fuision", "fuson" and "gusion" (g sits next to f) all find Fusion. The
/// query is compared with each word of the name by an edit distance that knows the keyboard: hitting a neighbouring
/// key or swapping two letters costs half a typo, a missing, extra or unrelated letter a whole one. The closer, the
/// higher the score, so the likeliest word wins.
/// </summary>
public static class Typo
{
    const double Near = 0.5, Swap = 0.6;

    // QWERTY rows with their stagger, for "is this key next to that one".
    static readonly (string Keys, double Offset)[] Rows = [("qwertyuiop", 0), ("asdfghjkl", 0.25), ("zxcvbnm", 0.75)];
    static readonly Dictionary<char, (double X, int Y)> Keys = Rows
        .SelectMany((row, y) => row.Keys.Select((c, x) => (c, pos: (row.Offset + x, y))))
        .ToDictionary(k => k.c, k => k.pos);

    internal static bool Adjacent(char a, char b) =>
        a != b && Keys.TryGetValue(a, out var p) && Keys.TryGetValue(b, out var q) && Math.Abs(p.Y - q.Y) <= 1 && Math.Abs(p.X - q.X) <= 1.0;

    /// <summary>0, or 68 minus 12 per typo: 62 for a neighbouring key, 56 for a missing letter, 50 at most 1.5 typos.</summary>
    public static double Score(string query, string name)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length < 4 || !q.All(char.IsAsciiLetter)) return 0; // short queries have too many near neighbours
        double allowed = q.Length < 6 ? 1.0 : 1.5;
        double best = double.MaxValue;
        var starts = FuzzyMatcher.WordStarts(name);
        var lower = name.ToLowerInvariant();
        foreach (var start in starts)
        {
            // The word, and the prefixes about as long as the query: "gusi" is on its way to "fusion".
            int end = start;
            while (end < lower.Length && char.IsAsciiLetterLower(lower[end])) end++;
            var word = lower[start..end];
            if (word.Length < 3) continue;
            best = Math.Min(best, Distance(q, word));
            for (int len = q.Length - 1; len <= q.Length + 1; len++)
                if (len >= 3 && len < word.Length) best = Math.Min(best, Distance(q, word[..len]) + 0.25); // a prefix counts a little less
        }
        return best <= allowed ? 68 - 12 * best : 0;
    }

    /// <summary>Keyboard-weighted edit distance with swaps (optimal string alignment).</summary>
    internal static double Distance(string a, string b)
    {
        var d = new double[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                double substitute = a[i - 1] == b[j - 1] ? 0 : Adjacent(a[i - 1], b[j - 1]) ? Near : 1;
                double v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + substitute);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + Swap);
                d[i, j] = v;
            }
        return d[a.Length, b.Length];
    }
}
