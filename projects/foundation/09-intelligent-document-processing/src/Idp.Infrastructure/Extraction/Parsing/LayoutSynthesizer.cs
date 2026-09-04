using System.Text;
using Idp.Application.Documents;
using Idp.Domain.Documents;

namespace Idp.Infrastructure.Extraction.Parsing;

/// <summary>
/// Builds a synthetic word-box layout for non-spatial inputs (plain text / CSV) so the same spatial
/// extractor and table detector can operate on them, and reconstructs reading-order text from word
/// boxes for spatial inputs. Coordinates use the same abstract page unit as the synthetic OCR format.
/// </summary>
public static class LayoutSynthesizer
{
    public const double CharWidth = 7.0;
    public const double RowHeight = 18.0;
    public const double LineGap = 6.0;
    public const double MarginX = 40.0;
    public const double MarginY = 40.0;

    /// <summary>Lay out plain text as monospaced word boxes, one text line per row.</summary>
    public static IReadOnlyList<OcrPage> FromPlainText(string text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var words = new List<WordBox>();
        double y = MarginY;
        foreach (var line in lines)
        {
            foreach (var token in Tokenize(line))
            {
                var x = MarginX + token.Start * CharWidth;
                words.Add(new WordBox(token.Text, x, y, token.Text.Length * CharWidth, RowHeight, 1));
            }
            y += RowHeight + LineGap;
        }
        var height = Math.Max(y + MarginY, 200);
        return new[] { new OcrPage(1, 1000, height, words) };
    }

    /// <summary>Lay out CSV as a grid of cell boxes with aligned columns.</summary>
    public static IReadOnlyList<OcrPage> FromCsv(string text, out string rawText)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None)
            .Where(l => l.Length > 0)
            .ToList();
        var words = new List<WordBox>();
        var sb = new StringBuilder();
        const double colWidth = 130.0;
        double y = MarginY;
        foreach (var line in lines)
        {
            var cells = SplitCsvLine(line);
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = cells[c].Trim();
                if (cell.Length == 0) continue;
                var x = MarginX + c * colWidth;
                words.Add(new WordBox(cell, x, y, Math.Max(cell.Length * CharWidth, 20), RowHeight, 1));
                sb.Append(cell);
                if (c < cells.Count - 1) sb.Append(' ');
            }
            sb.Append('\n');
            y += RowHeight + LineGap;
        }
        rawText = sb.ToString();
        var height = Math.Max(y + MarginY, 200);
        return new[] { new OcrPage(1, 1000, height, words) };
    }

    /// <summary>Reconstruct reading-order text from word boxes (rows top-to-bottom, words left-to-right).</summary>
    public static string ComposeText(IReadOnlyList<OcrPage> pages)
    {
        var sb = new StringBuilder();
        foreach (var page in pages.OrderBy(p => p.Page))
        {
            foreach (var row in GroupRows(page.Words))
            {
                sb.AppendLine(string.Join(" ", row.OrderBy(w => w.X).Select(w => w.Text)));
            }
        }
        return sb.ToString();
    }

    /// <summary>Group words on a page into visual rows by vertical proximity.</summary>
    public static IEnumerable<List<WordBox>> GroupRows(IReadOnlyList<WordBox> words)
    {
        var rows = new List<List<WordBox>>();
        foreach (var w in words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var row = rows.FirstOrDefault(r => r[0].SharesRowWith(w));
            if (row is null) rows.Add(new List<WordBox> { w });
            else row.Add(w);
        }
        return rows;
    }

    private readonly record struct Token(string Text, int Start);

    private static IEnumerable<Token> Tokenize(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            yield return new Token(line[start..i], start);
        }
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
