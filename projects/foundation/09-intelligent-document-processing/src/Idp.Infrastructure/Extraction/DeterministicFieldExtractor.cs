using System.Globalization;
using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Domain.Documents;
using Idp.Infrastructure.Extraction.Parsing;

namespace Idp.Infrastructure.Extraction;

/// <summary>
/// The default, deterministic field extractor. It combines three transparent strategies — learned
/// per-supplier anchors (the feedback loop), built-in label anchors, and regex fallbacks over the
/// reading-order text — plus a spatial table detector for line items. Every produced field records
/// its value, a normalised value, a confidence, the strategy that produced it and the source
/// span/box (the evidence a reviewer sees). No ML model and no network are involved.
/// </summary>
public sealed class DeterministicFieldExtractor : IFieldExtractor
{
    private static readonly string[] CurrencyCodes = { "KES", "USD", "EUR", "GBP" };

    private const double LearnedAnchorConfidence = 0.98;
    private const double AnchorConfidence = 0.90;
    private const double RegexConfidence = 0.70;
    private const double PositionalConfidence = 0.58;

    private readonly SpatialTableDetector _tableDetector = new();

    public ExtractionResult Extract(
        DocumentType type, DocumentContent content, IReadOnlyList<ExtractionHint> hints)
    {
        var page = content.Pages.OrderByDescending(p => p.Words.Count).FirstOrDefault();
        var rows = page is null
            ? new List<List<WordBox>>()
            : LayoutSynthesizer.GroupRows(page.Words)
                .Select(r => r.OrderBy(w => w.X).ToList())
                .OrderBy(r => r[0].Y)
                .ToList();

        var fields = new List<ExtractedField>();
        void Add(ExtractedField? f) { if (f is not null) fields.Add(f); }

        switch (type)
        {
            case DocumentType.Invoice:
                Add(SupplierName(type, rows, content, hints));
                Add(Scalar(type, FieldKeys.SupplierTaxId, rows, hints, Kind.TaxId,
                    "tax id", "taxid", "pin", "vat no", "tax number"));
                Add(Scalar(type, FieldKeys.InvoiceNumber, rows, hints, Kind.Text,
                    "invoice number", "invoice no", "inv no", "invoice"));
                Add(Scalar(type, FieldKeys.InvoiceDate, rows, hints, Kind.Date,
                    "invoice date", "date issued", "issue date", "date"));
                Add(Scalar(type, FieldKeys.DueDate, rows, hints, Kind.Date,
                    "due date", "payment due", "due"));
                Add(Currency(type, FieldKeys.Currency, rows, content, hints));
                Add(Scalar(type, FieldKeys.PoReference, rows, hints, Kind.Text,
                    "po reference", "po ref", "purchase order", "order ref", "po number"));
                Add(Scalar(type, FieldKeys.Subtotal, rows, hints, Kind.Amount,
                    "subtotal", "sub total", "sub-total", "net total"));
                Add(Scalar(type, FieldKeys.Tax, rows, hints, Kind.Amount,
                    "tax amount", "vat amount", "tax", "vat"));
                Add(Scalar(type, FieldKeys.Total, rows, hints, Kind.Amount,
                    "grand total", "total due", "amount due", "total"));
                Add(Scalar(type, FieldKeys.BankDetails, rows, hints, Kind.Text,
                    "bank details", "bank account", "iban", "account"));
                break;

            case DocumentType.PurchaseOrder:
                Add(SupplierName(type, rows, content, hints));
                Add(Scalar(type, FieldKeys.PoNumber, rows, hints, Kind.Text,
                    "po number", "purchase order number", "order number", "po no", "purchase order"));
                Add(Scalar(type, FieldKeys.PoDate, rows, hints, Kind.Date,
                    "order date", "po date", "date"));
                Add(Scalar(type, FieldKeys.Buyer, rows, hints, Kind.Text,
                    "buyer", "bill to", "ship to", "ordered by"));
                Add(Currency(type, FieldKeys.Currency, rows, content, hints));
                Add(Scalar(type, FieldKeys.Total, rows, hints, Kind.Amount,
                    "order total", "grand total", "total"));
                break;

            case DocumentType.DeliveryNote:
                Add(SupplierName(type, rows, content, hints));
                Add(Scalar(type, FieldKeys.DnNumber, rows, hints, Kind.Text,
                    "delivery note number", "dn number", "note number", "delivery note", "dn no"));
                Add(Scalar(type, FieldKeys.DnDate, rows, hints, Kind.Date,
                    "delivery date", "dn date", "date"));
                Add(Scalar(type, FieldKeys.PoReference, rows, hints, Kind.Text,
                    "po reference", "po ref", "purchase order", "order ref", "po number"));
                break;
        }

        var lineItems = ExtractLineItems(content);
        var currency = fields.FirstOrDefault(f => f.FieldKey == FieldKeys.Currency)?.NormalizedValue;
        var documentValue = ResolveDocumentValue(fields, lineItems);

        return new ExtractionResult(fields, lineItems, currency, documentValue);
    }

    private enum Kind { Text, Amount, Date, Currency, TaxId }

    private ExtractedField? Scalar(
        DocumentType type, string key, List<List<WordBox>> rows,
        IReadOnlyList<ExtractionHint> hints, Kind kind, params string[] labels)
    {
        var required = DocumentSchemas.IsRequired(type, key);
        var learned = hints.FirstOrDefault(h => h.FieldKey == key)?.AnchorText;
        var ordered = learned is null ? labels : new[] { learned }.Concat(labels).ToArray();

        foreach (var label in ordered)
        {
            foreach (var (region, box) in FindAnchors(rows, label))
            {
                if (!Normalize(kind, region, out var raw, out var norm)) continue;

                var isLearned = learned is not null &&
                                label.Equals(learned, StringComparison.OrdinalIgnoreCase);
                var strategy = isLearned
                    ? ExtractionStrategy.LearnedAnchor
                    : ExtractionStrategy.Anchor;
                var confidence = isLearned ? LearnedAnchorConfidence : AnchorConfidence;
                return new ExtractedField(
                    key, raw, norm, confidence, strategy, required, region, box);
            }
        }

        return null;
    }

    private ExtractedField? SupplierName(
        DocumentType type, List<List<WordBox>> rows, DocumentContent content,
        IReadOnlyList<ExtractionHint> hints)
    {
        var anchored = Scalar(type, FieldKeys.SupplierName, rows, hints, Kind.Text,
            "supplier", "from", "vendor", "seller", "supplier name");
        if (anchored is not null) return anchored;

        // Positional fallback: the first meaningful line under the document-type title.
        foreach (var row in rows)
        {
            var text = string.Join(" ", row.Select(w => w.Text)).Trim();
            var norm = new string(text.ToLowerInvariant().Where(char.IsLetter).ToArray());
            if (norm.Length < 4) continue;
            if (norm is "taxinvoice" or "invoice" or "purchaseorder" or "deliverynote") continue;
            var required = DocumentSchemas.IsRequired(type, FieldKeys.SupplierName);
            return new ExtractedField(FieldKeys.SupplierName, text, text.Trim(),
                PositionalConfidence, ExtractionStrategy.Positional, required, text, row[0]);
        }
        return null;
    }

    private ExtractedField? Currency(
        DocumentType type, string key, List<List<WordBox>> rows, DocumentContent content,
        IReadOnlyList<ExtractionHint> hints)
    {
        var anchored = Scalar(type, key, rows, hints, Kind.Currency, "currency", "ccy");
        if (anchored is not null) return anchored;

        // Regex fallback: first known currency code appearing in the reading-order text.
        var text = (content.RawText ?? string.Empty).ToUpperInvariant();
        foreach (var code in CurrencyCodes)
        {
            if (text.Contains(code, StringComparison.Ordinal))
            {
                var required = DocumentSchemas.IsRequired(type, key);
                return new ExtractedField(key, code, code, RegexConfidence,
                    ExtractionStrategy.Regex, required, code);
            }
        }
        return null;
    }

    private IReadOnlyList<LineItem> ExtractLineItems(DocumentContent content)
    {
        var table = _tableDetector.Detect(content);
        if (table is null || table.Rows.Count == 0) return Array.Empty<LineItem>();

        var descCol = table.FindColumn(h => h.Contains("desc") || h.Contains("item"))
                      ?? table.Columns.FirstOrDefault()?.Index;
        var qtyCol = table.FindColumn(h => h.Contains("qty") || h.Contains("quant"));
        var priceCol = table.FindColumn(h =>
            (h.Contains("unit") || h.Contains("price") || (h.Contains("rate") && !h.Contains("tax"))));
        var taxCol = table.FindColumn(h => h.Contains("tax") || h.Contains("vat"));
        var totalCol = table.FindColumn(h => h.Contains("total") || h.Contains("amount") || h.Contains("line"));

        var items = new List<LineItem>();
        var lineNo = 1;
        foreach (var row in table.Rows)
        {
            string? Cell(int? col) => col is { } c
                ? row.FirstOrDefault(x => x.Column == c)?.Text
                : null;

            var description = Cell(descCol);
            var qty = ParseDecimal(Cell(qtyCol));
            var price = ParseDecimal(Cell(priceCol));
            var lineTotal = ParseDecimal(Cell(totalCol));
            var taxRate = ParsePercent(Cell(taxCol));

            if (description is null && qty is null && price is null && lineTotal is null) continue;

            var numericCount = new[] { qty, price, lineTotal }.Count(v => v is not null);
            var confidence = 0.5 + 0.15 * numericCount;
            items.Add(new LineItem(lineNo++, description?.Trim(), qty, price, lineTotal, taxRate,
                Math.Min(1.0, confidence)));
        }
        return items;
    }

    private static decimal? ResolveDocumentValue(
        IReadOnlyList<ExtractedField> fields, IReadOnlyList<LineItem> lineItems)
    {
        var total = fields.FirstOrDefault(f => f.FieldKey == FieldKeys.Total);
        if (total is not null && DocumentFieldReader.ParseAmount(total.NormalizedValue ?? total.RawValue) is { } t)
            return t;
        if (lineItems.Count > 0)
            return decimal.Round(lineItems.Where(l => l.LineTotal is not null).Sum(l => l.LineTotal!.Value), 2);
        var subtotal = fields.FirstOrDefault(f => f.FieldKey == FieldKeys.Subtotal);
        return subtotal is not null
            ? DocumentFieldReader.ParseAmount(subtotal.NormalizedValue ?? subtotal.RawValue)
            : null;
    }

    /// <summary>Enumerate every place a label phrase appears, yielding the value words that follow it
    /// (or, when the label is alone on its row, the aligned words on the row below).</summary>
    private static IEnumerable<(string region, WordBox box)> FindAnchors(
        List<List<WordBox>> rows, string labelPhrase)
    {
        var labelTokens = labelPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken).Where(t => t.Length > 0).ToArray();
        if (labelTokens.Length == 0) yield break;

        foreach (var row in rows)
        {
            var norm = row.Select(w => NormalizeToken(w.Text)).ToList();
            for (var i = 0; i + labelTokens.Length <= norm.Count; i++)
            {
                var match = true;
                for (var j = 0; j < labelTokens.Length; j++)
                {
                    if (norm[i + j] != labelTokens[j]) { match = false; break; }
                }
                if (!match) continue;

                var after = row.Skip(i + labelTokens.Length).ToList();
                if (after.Count > 0)
                {
                    yield return (string.Join(" ", after.Select(w => w.Text)).Trim(), after[0]);
                    continue;
                }

                // Value on the row below, aligned under the label.
                var labelX = row[i].X;
                var labelY = row[i].Y;
                var below = rows
                    .Where(r => r[0].Y > labelY)
                    .OrderBy(r => r[0].Y)
                    .FirstOrDefault(r => r.Any(w => Math.Abs(w.X - labelX) < 60));
                if (below is not null)
                {
                    var vs = below.Where(w => w.X >= labelX - 60).OrderBy(w => w.X).ToList();
                    if (vs.Count > 0)
                        yield return (string.Join(" ", vs.Select(w => w.Text)).Trim(), vs[0]);
                }
            }
        }
    }

    private static bool Normalize(Kind kind, string region, out string? raw, out string? normalized)
    {
        raw = region.Trim();
        normalized = null;
        if (string.IsNullOrWhiteSpace(region)) return false;

        switch (kind)
        {
            case Kind.Text:
                normalized = raw;
                return raw!.Length > 0;

            case Kind.TaxId:
                var idToken = region.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (idToken is null) return false;
                raw = idToken;
                normalized = idToken.ToUpperInvariant();
                return true;

            case Kind.Amount:
                if (!TryAmountRegion(region, out var amount)) return false;
                raw = region.Trim();
                normalized = amount.ToString(CultureInfo.InvariantCulture);
                return true;

            case Kind.Date:
                var date = DocumentFieldReader.ParseDate(region)
                           ?? DocumentFieldReader.ParseDate(
                               string.Join(" ",
                                   region.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3)));
                if (date is null) return false;
                raw = region.Trim();
                normalized = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;

            case Kind.Currency:
                var token = region.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (token is null) return false;
                token = token.ToUpperInvariant().Trim('.', ':');
                token = token switch { "$" => "USD", "€" => "EUR", "£" => "GBP", _ => token };
                if (!CurrencyCodes.Contains(token)) return false;
                raw = token;
                normalized = token;
                return true;
        }
        return false;
    }

    /// <summary>Parse an amount region, tolerating a leading currency code/symbol.</summary>
    private static bool TryAmountRegion(string region, out decimal value)
    {
        value = 0;
        var tokens = region.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0 &&
               (CurrencyCodes.Contains(tokens[0].ToUpperInvariant()) ||
                tokens[0] is "$" or "€" or "£"))
            tokens.RemoveAt(0);
        if (tokens.Count == 0) return false;

        var first = tokens[0];
        if (!(char.IsDigit(first[0]) || first[0] is '-' or '.')) return false;
        var parsed = DocumentFieldReader.ParseAmount(first);
        if (parsed is null) return false;
        value = parsed.Value;
        return true;
    }

    private static decimal? ParseDecimal(string? text) => DocumentFieldReader.ParseAmount(text);

    private static decimal? ParsePercent(string? text)
    {
        var v = DocumentFieldReader.ParseAmount(text);
        if (v is null) return null;
        return v.Value > 1m ? v.Value / 100m : v.Value; // "16" or "16%" -> 0.16
    }

    private static string NormalizeToken(string text) =>
        new(text.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
