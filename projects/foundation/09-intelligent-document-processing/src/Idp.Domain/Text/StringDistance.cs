namespace Idp.Domain.Text;

/// <summary>
/// Pure string-distance functions used by fuzzy supplier master-data matching.
/// Implemented in-repo (no external dependency) so the matching behaviour is fully testable.
/// </summary>
public static class StringDistance
{
    /// <summary>Classic Levenshtein edit distance.</summary>
    public static int Levenshtein(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>Levenshtein similarity normalised to [0,1] (1 == identical).</summary>
    public static double NormalizedLevenshtein(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        var max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - ((double)Levenshtein(a, b) / max);
    }

    /// <summary>Jaro similarity in [0,1].</summary>
    public static double Jaro(string s1, string s2)
    {
        s1 ??= string.Empty;
        s2 ??= string.Empty;
        if (s1.Length == 0 && s2.Length == 0) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;

        var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchDistance < 0) matchDistance = 0;

        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];
        var matches = 0;

        for (var i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, s2.Length);
            for (var j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0.0;

        double transpositions = 0;
        var k = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }
        transpositions /= 2;

        return (
            (matches / (double)s1.Length) +
            (matches / (double)s2.Length) +
            ((matches - transpositions) / matches)) / 3.0;
    }

    /// <summary>Jaro-Winkler similarity (Jaro with a common-prefix bonus), in [0,1].</summary>
    public static double JaroWinkler(string s1, string s2, double prefixScale = 0.1)
    {
        var jaro = Jaro(s1, s2);
        s1 ??= string.Empty;
        s2 ??= string.Empty;
        var prefix = 0;
        var maxPrefix = Math.Min(4, Math.Min(s1.Length, s2.Length));
        for (var i = 0; i < maxPrefix; i++)
        {
            if (s1[i] == s2[i]) prefix++;
            else break;
        }
        return jaro + (prefix * prefixScale * (1 - jaro));
    }
}
