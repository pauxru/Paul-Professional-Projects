namespace Collab.Domain.Diffing;

public enum DiffKind
{
    Equal = 0,
    Insert = 1,
    Delete = 2
}

/// <summary>A contiguous run of equal / inserted / deleted text produced by the diff.</summary>
public sealed record DiffSegment(DiffKind Kind, string Text);

/// <summary>
/// A minimal, correct diff based on a Longest-Common-Subsequence dynamic program. Not the fastest
/// algorithm (Myers is better on large inputs) but easy to prove correct and adequate for
/// version-history diffing. Works at character or line granularity.
/// </summary>
public static class DiffEngine
{
    public static IReadOnlyList<DiffSegment> DiffChars(string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var a = oldText.Select(c => c.ToString()).ToArray();
        var b = newText.Select(c => c.ToString()).ToArray();
        return Group(DiffTokens(a, b), string.Empty);
    }

    public static IReadOnlyList<DiffSegment> DiffLines(string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        return Group(DiffTokens(a, b), "\n");
    }

    private static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Token-level edit script via LCS backtracking.</summary>
    private static List<(DiffKind Kind, string Token)> DiffTokens(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        // lcs[i, j] = length of LCS of a[i..] and b[j..].
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<(DiffKind, string)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                result.Add((DiffKind.Equal, a[x]));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add((DiffKind.Delete, a[x]));
                x++;
            }
            else
            {
                result.Add((DiffKind.Insert, b[y]));
                y++;
            }
        }
        while (x < n) result.Add((DiffKind.Delete, a[x++]));
        while (y < m) result.Add((DiffKind.Insert, b[y++]));
        return result;
    }

    private static List<DiffSegment> Group(List<(DiffKind Kind, string Token)> tokens, string sep)
    {
        var segments = new List<DiffSegment>();
        var i = 0;
        while (i < tokens.Count)
        {
            var kind = tokens[i].Kind;
            var run = new List<string>();
            while (i < tokens.Count && tokens[i].Kind == kind)
            {
                run.Add(tokens[i].Token);
                i++;
            }
            segments.Add(new DiffSegment(kind, string.Join(sep, run)));
        }
        return segments;
    }

    /// <summary>Human-readable unified rendering (+/-/space prefixes) for runbooks and history views.</summary>
    public static string ToUnified(IReadOnlyList<DiffSegment> segments)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var seg in segments)
        {
            var prefix = seg.Kind switch
            {
                DiffKind.Insert => "+",
                DiffKind.Delete => "-",
                _ => " "
            };
            sb.Append(prefix).Append(seg.Text).Append('\n');
        }
        return sb.ToString();
    }
}
