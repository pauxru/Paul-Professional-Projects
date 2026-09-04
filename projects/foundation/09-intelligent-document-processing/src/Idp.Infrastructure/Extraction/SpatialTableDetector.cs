using Idp.Application.Documents;
using Idp.Domain.Documents;
using Idp.Infrastructure.Extraction.Parsing;

namespace Idp.Infrastructure.Extraction;

/// <summary>A detected table column, anchored at a horizontal centre position.</summary>
public sealed record TableColumn(string Header, double CenterX, int Index);

/// <summary>A single cell: the words that fell under a column on a given row.</summary>
public sealed record TableCell(int Column, string Text, WordBox Box);

/// <summary>The result of spatial table detection: header columns and the data rows beneath them.</summary>
public sealed class DetectedTable
{
    public IReadOnlyList<TableColumn> Columns { get; }
    public IReadOnlyList<IReadOnlyList<TableCell>> Rows { get; }
    public double HeaderY { get; }

    public DetectedTable(
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<IReadOnlyList<TableCell>> rows,
        double headerY)
    {
        Columns = columns;
        Rows = rows;
        HeaderY = headerY;
    }

    public int? FindColumn(Func<string, bool> headerPredicate) =>
        Columns.FirstOrDefault(c => headerPredicate(c.Header.ToLowerInvariant()))?.Index;
}

/// <summary>
/// Groups recognised words into a table by coordinates: it finds the header row (the row carrying the
/// column captions), turns each header word into a column anchor, then assigns every word in the rows
/// beneath to its nearest column by horizontal centre. Detection stops at the totals block. This is
/// what makes extraction genuinely spatial rather than regex-over-text.
/// </summary>
public sealed class SpatialTableDetector
{
    private static readonly string[] HeaderKeywords =
        { "description", "item", "qty", "quantity", "unit", "price", "rate", "amount", "total", "tax" };

    private static readonly string[] StopKeywords =
        { "subtotal", "sub-total", "tax", "total", "grandtotal", "grand", "balance", "amountdue",
          "bankdetails", "bank", "notes", "authorised", "signature" };

    public DetectedTable? Detect(DocumentContent content)
    {
        if (!content.HasLayout) return null;
        var page = content.Pages.OrderByDescending(p => p.Words.Count).First();
        var rows = LayoutSynthesizer.GroupRows(page.Words)
            .Select(r => r.OrderBy(w => w.X).ToList())
            .OrderBy(r => r[0].Y)
            .ToList();

        var headerIndex = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var hits = rows[i].Count(w => HeaderKeywords.Contains(Normalize(w.Text)));
            if (hits >= 2) { headerIndex = i; break; }
        }
        if (headerIndex < 0) return null;

        var headerRow = rows[headerIndex];
        var columns = headerRow
            .Select((w, idx) => new TableColumn(w.Text, w.CenterX, idx))
            .ToList();

        var dataRows = new List<IReadOnlyList<TableCell>>();
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 0) continue;

            var firstNorm = Normalize(row[0].Text);
            if (StopKeywords.Contains(firstNorm)) break;

            var cells = AssignCells(row, columns);
            var hasNumeric = cells.Any(c => ContainsNumber(c.Text));
            if (!hasNumeric) continue; // skip stray non-line rows

            dataRows.Add(cells);
        }

        return new DetectedTable(columns, dataRows, headerRow[0].Y);
    }

    private static List<TableCell> AssignCells(
        IReadOnlyList<WordBox> rowWords, IReadOnlyList<TableColumn> columns)
    {
        var buckets = new Dictionary<int, List<WordBox>>();
        foreach (var w in rowWords)
        {
            var nearest = columns
                .OrderBy(c => Math.Abs(c.CenterX - w.CenterX))
                .First();
            if (!buckets.TryGetValue(nearest.Index, out var list))
                buckets[nearest.Index] = list = new List<WordBox>();
            list.Add(w);
        }

        var cells = new List<TableCell>();
        foreach (var (index, words) in buckets)
        {
            var ordered = words.OrderBy(w => w.X).ToList();
            var text = string.Join(" ", ordered.Select(w => w.Text));
            var box = ordered[0];
            cells.Add(new TableCell(index, text, box));
        }
        return cells.OrderBy(c => c.Column).ToList();
    }

    private static bool ContainsNumber(string text) =>
        text.Any(char.IsDigit) &&
        text.Count(char.IsDigit) >= 1 &&
        !text.All(char.IsLetter);

    private static string Normalize(string text) =>
        new(text.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
