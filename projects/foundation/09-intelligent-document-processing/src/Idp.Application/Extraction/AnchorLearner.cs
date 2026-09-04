using Idp.Application.Documents;
using Idp.Domain.Documents;

namespace Idp.Application.Extraction;

/// <summary>
/// Learns an extraction anchor from a human correction. Given the corrected value, it locates that
/// value's word box in the document layout and returns the label word immediately to its left (or,
/// failing that, directly above). That label becomes a per-supplier learned anchor so the same
/// supplier's next document extracts the field correctly. This is the mechanism behind the feedback
/// loop.
/// </summary>
public static class AnchorLearner
{
    public static string? FindAnchor(DocumentContent content, string? correctedValue)
    {
        if (string.IsNullOrWhiteSpace(correctedValue) || !content.HasLayout) return null;

        var token = Normalize(correctedValue.Split(
            new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? correctedValue);
        if (token.Length == 0) return null;

        var words = content.AllWords;
        WordBox? valueWord = null;
        foreach (var w in words)
        {
            var n = Normalize(w.Text);
            if (n == token || (token.Length >= 3 && n.Contains(token)))
            {
                valueWord = w;
                break;
            }
        }
        if (valueWord is not { } vw) return null;

        // Nearest label word to the left on the same row.
        WordBox? left = null;
        foreach (var w in words)
        {
            if (ReferenceEquals(w.Text, vw.Text) && w.X == vw.X && w.Y == vw.Y) continue;
            if (!w.SharesRowWith(vw)) continue;
            if (w.Right > vw.X + 0.5) continue; // must be to the left
            if (left is null || w.Right > left.Value.Right) left = w;
        }
        if (left is { } l && LooksLikeLabel(l.Text)) return Clean(l.Text);

        // Otherwise the nearest label word directly above.
        WordBox? above = null;
        foreach (var w in words)
        {
            if (w.Page != vw.Page) continue;
            if (w.Bottom > vw.Y - 0.5) continue; // must be above
            if (Math.Abs(w.CenterX - vw.CenterX) > Math.Max(vw.Width, 40)) continue;
            if (above is null || w.Y > above.Value.Y) above = w;
        }
        if (above is { } a && LooksLikeLabel(a.Text)) return Clean(a.Text);

        return null;
    }

    private static bool LooksLikeLabel(string text)
    {
        var cleaned = Clean(text);
        return cleaned.Length >= 2 && cleaned.Any(char.IsLetter);
    }

    private static string Clean(string text) => text.Trim().TrimEnd(':', '#', '-').Trim();

    private static string Normalize(string text) =>
        new string(text.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
}
