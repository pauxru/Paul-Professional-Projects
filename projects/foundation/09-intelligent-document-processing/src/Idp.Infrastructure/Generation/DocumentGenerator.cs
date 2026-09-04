using System.Globalization;
using System.Text.Json;
using Idp.Application.Extraction;
using Idp.Domain.Documents;
using Idp.Domain.Text;

namespace Idp.Infrastructure.Generation;

/// <summary>A supplier in the generated master data.</summary>
public sealed record MasterSupplier(
    string Name, string TaxId, string Currency, string BankAccount, string[] Aliases);

/// <summary>A generated synthetic document plus the ground truth used to measure accuracy.</summary>
public sealed record SyntheticDocument(
    string FileName,
    string ContentType,
    byte[] Bytes,
    DocumentType Type,
    string Template,
    bool Degraded,
    IReadOnlyDictionary<string, string> Expected);

/// <summary>
/// Produces a realistic, fully deterministic corpus of synthetic invoices, purchase orders and
/// delivery notes in the spatial <c>.ocr.json</c> format, from several supplier templates with
/// varying layouts, plus deliberately degraded variants (missing fields, misaligned columns,
/// OCR-style character confusions and wrong totals). The generator also emits ground-truth field
/// values so classification/extraction/STP accuracy can be measured for real over the corpus.
/// No randomness: the same corpus is produced every run, so the measured numbers are reproducible.
/// </summary>
public sealed class DocumentGenerator
{
    public IReadOnlyList<MasterSupplier> Suppliers { get; } = new List<MasterSupplier>
    {
        new("Rift Valley Supplies Ltd", TaxIdFormat.Build("KE", "001234571"), "KES",
            "KES-ACCT-4455-1", new[] { "Rift Valley Supplies", "RVS Ltd" }),
        new("Nairobi Steel Traders", TaxIdFormat.Build("KE", "104588213"), "KES",
            "KES-ACCT-8891-2", new[] { "Nairobi Steel" }),
        new("Mombasa Imports Co", TaxIdFormat.Build("KE", "220145900"), "USD",
            "USD-ACCT-2201-7", new[] { "Mombasa Imports" }),
        new("Coastal Hardware Ltd", TaxIdFormat.Build("KE", "330912844"), "KES",
            "KES-ACCT-3312-9", new[] { "Coastal Hardware" }),
    };

    private static readonly (string Desc, decimal Price)[] Catalog =
    {
        ("Steel Bolts M10", 12.50m),
        ("Hex Nuts M10", 3.20m),
        ("Flat Washers 10mm", 1.10m),
        ("Galvanised Pipe 2in", 45.00m),
        ("Angle Bracket L", 8.75m),
        ("Welding Rods 3.2mm", 22.40m),
    };

    private DateTime _date = new(2024, 5, 10, 0, 0, 0, DateTimeKind.Utc);
    private int _seq = 1000;

    public IReadOnlyList<SyntheticDocument> GenerateCorpus()
    {
        var docs = new List<SyntheticDocument>();

        // Four fully matched invoice <-> PO <-> delivery-note triples (clean, three-way matchable).
        for (var i = 0; i < Suppliers.Count; i++)
        {
            var supplier = Suppliers[i];
            var poNumber = $"PO-{5000 + i}";
            var lines = PickLines(i);
            var style = i % 3;

            docs.Add(PurchaseOrder(supplier, poNumber, lines, style));
            docs.Add(DeliveryNote(supplier, poNumber, lines, style, deliveredFraction: 1.0m));
            docs.Add(Invoice(supplier, poNumber, lines, style, degraded: null));
        }

        // Two standalone clean invoices (no PO reference) — pure straight-through candidates.
        docs.Add(Invoice(Suppliers[0], null, PickLines(1), style: 0, degraded: null));
        docs.Add(Invoice(Suppliers[1], null, PickLines(2), style: 1, degraded: null));

        // Deliberately degraded invoices exercising each guardrail.
        docs.Add(Invoice(Suppliers[0], null, PickLines(0), 0, Degradation.WrongTotal));
        docs.Add(Invoice(Suppliers[1], null, PickLines(2), 1, Degradation.MissingInvoiceDate));
        docs.Add(Invoice(Suppliers[3], null, PickLines(3), 0, Degradation.OcrSupplierName));
        docs.Add(Invoice(Suppliers[2], null, PickLines(4), 2, Degradation.MisalignedColumns));
        docs.Add(Invoice(Suppliers[3], null, PickLines(1), 0, Degradation.InvalidTaxId));

        return docs;
    }

    private enum Degradation { WrongTotal, MissingInvoiceDate, OcrSupplierName, MisalignedColumns, InvalidTaxId }

    private (string Desc, decimal Qty, decimal Price)[] PickLines(int seed)
    {
        var a = Catalog[seed % Catalog.Length];
        var b = Catalog[(seed + 2) % Catalog.Length];
        return new[]
        {
            (a.Desc, (decimal)(2 + seed % 4), a.Price),
            (b.Desc, (decimal)(3 + seed % 3), b.Price),
        };
    }

    private SyntheticDocument Invoice(
        MasterSupplier supplier, string? poNumber,
        (string Desc, decimal Qty, decimal Price)[] lines, int style, Degradation? degraded)
    {
        var invNo = $"INV-{_seq++}";
        var invDate = _date; _date = _date.AddDays(2);
        var dueDate = invDate.AddDays(30);
        var taxRate = supplier.Currency == "USD" ? 0m : 0.16m;

        var subtotal = lines.Sum(l => decimal.Round(l.Qty * l.Price, 2));
        var tax = decimal.Round(subtotal * taxRate, 2);
        var total = subtotal + tax;
        var printedTotal = degraded == Degradation.WrongTotal ? total + 50.00m : total;

        var supplierName = degraded == Degradation.OcrSupplierName
            ? OcrConfuse(supplier.Name)
            : supplier.Name;
        var taxId = degraded == Degradation.InvalidTaxId
            ? Corrupt(supplier.TaxId)
            : supplier.TaxId;

        var ocr = new Ocr();
        ocr.Title("TAX INVOICE");
        ocr.Free(supplierName, 40);
        ocr.Labeled("From:", supplierName);
        ocr.Labeled("Tax ID:", taxId);
        ocr.Labeled(style == 1 ? "Invoice No:" : "Invoice Number:", invNo);
        if (degraded != Degradation.MissingInvoiceDate)
            ocr.Labeled("Invoice Date:", Iso(invDate));
        ocr.Labeled("Due Date:", Iso(dueDate));
        ocr.Labeled("Currency:", supplier.Currency);
        if (poNumber is not null) ocr.Labeled("PO Reference:", poNumber);
        ocr.Labeled("Bill To:", "Acme Manufacturing");
        ocr.Blank();

        WriteTable(ocr, lines, taxRate, style, degraded == Degradation.MisalignedColumns);

        ocr.Labeled("Subtotal:", Money(subtotal), 470);
        if (taxRate > 0) ocr.Labeled("Tax:", Money(tax), 470);
        ocr.Labeled("Total:", Money(printedTotal), 470);
        ocr.Labeled("Bank Details:", supplier.BankAccount);

        var expected = new Dictionary<string, string>
        {
            [FieldKeys.SupplierName] = supplier.Name,
            [FieldKeys.InvoiceNumber] = invNo,
            [FieldKeys.Currency] = supplier.Currency,
            [FieldKeys.Total] = Money(printedTotal),
        };
        if (degraded != Degradation.MissingInvoiceDate)
            expected[FieldKeys.InvoiceDate] = Iso(invDate);

        var template = $"{supplier.Name} (style {style})";
        return new SyntheticDocument(
            $"invoice-{invNo}.ocr.json", OcrContentType, ocr.ToBytes(),
            DocumentType.Invoice, template, degraded is not null, expected);
    }

    private SyntheticDocument PurchaseOrder(
        MasterSupplier supplier, string poNumber,
        (string Desc, decimal Qty, decimal Price)[] lines, int style)
    {
        var poDate = _date;
        var total = lines.Sum(l => decimal.Round(l.Qty * l.Price, 2));

        var ocr = new Ocr();
        ocr.Title("PURCHASE ORDER");
        ocr.Free("Acme Manufacturing", 40);
        ocr.Labeled("Supplier:", supplier.Name);
        ocr.Labeled(style == 1 ? "PO No:" : "PO Number:", poNumber);
        ocr.Labeled("Order Date:", Iso(poDate));
        ocr.Labeled("Buyer:", "Acme Manufacturing");
        ocr.Labeled("Currency:", supplier.Currency);
        ocr.Blank();

        WriteTable(ocr, lines, taxRate: 0m, style, misalign: false, includeTax: false);
        ocr.Labeled("Total:", Money(total), 470);

        var expected = new Dictionary<string, string>
        {
            [FieldKeys.SupplierName] = supplier.Name,
            [FieldKeys.PoNumber] = poNumber,
            [FieldKeys.PoDate] = Iso(poDate),
            [FieldKeys.Total] = Money(total),
        };
        return new SyntheticDocument(
            $"po-{poNumber}.ocr.json", OcrContentType, ocr.ToBytes(),
            DocumentType.PurchaseOrder, $"{supplier.Name} (style {style})", false, expected);
    }

    private SyntheticDocument DeliveryNote(
        MasterSupplier supplier, string poNumber,
        (string Desc, decimal Qty, decimal Price)[] lines, int style, decimal deliveredFraction)
    {
        var dnNumber = $"DN-{7000 + _seq % 1000}";
        var dnDate = _date.AddDays(1);

        var ocr = new Ocr();
        ocr.Title("DELIVERY NOTE");
        ocr.Free(supplier.Name, 40);
        ocr.Labeled("Delivery Note Number:", dnNumber);
        ocr.Labeled("Delivery Date:", Iso(dnDate));
        ocr.Labeled("PO Reference:", poNumber);
        ocr.Blank();

        ocr.Cells(("Description", 40), ("Quantity", 380));
        foreach (var line in lines)
        {
            var delivered = decimal.Round(line.Qty * deliveredFraction, 0);
            ocr.Cells((line.Desc, 40), (delivered.ToString("0", CultureInfo.InvariantCulture), 380));
        }

        var expected = new Dictionary<string, string>
        {
            [FieldKeys.DnNumber] = dnNumber,
            [FieldKeys.DnDate] = Iso(dnDate),
            [FieldKeys.PoReference] = poNumber,
        };
        return new SyntheticDocument(
            $"dn-{dnNumber}.ocr.json", OcrContentType, ocr.ToBytes(),
            DocumentType.DeliveryNote, $"{supplier.Name} (style {style})", false, expected);
    }

    private static void WriteTable(
        Ocr ocr, (string Desc, decimal Qty, decimal Price)[] lines, decimal taxRate, int style,
        bool misalign, bool includeTax = true)
    {
        var hasTax = includeTax && taxRate > 0;
        double descX = 40, qtyX = 380, priceX = 470, taxX = 610, totalX = 720;
        if (misalign) { priceX += 55; totalX -= 30; } // deliberately shift columns

        if (style == 2)
        {
            ocr.Cells(("Item", descX), ("Quantity", qtyX), ("Rate", priceX), ("Amount", totalX));
        }
        else if (hasTax)
        {
            ocr.Cells(("Description", descX), ("Qty", qtyX), ("UnitPrice", priceX),
                ("TaxRate", taxX), ("LineTotal", totalX));
        }
        else
        {
            ocr.Cells(("Description", descX), ("Qty", qtyX), ("UnitPrice", priceX),
                ("LineTotal", totalX));
        }

        foreach (var line in lines)
        {
            var lineTotal = decimal.Round(line.Qty * line.Price, 2);
            var cells = new List<(string, double)>
            {
                (line.Desc, descX),
                (line.Qty.ToString("0.##", CultureInfo.InvariantCulture), qtyX),
                (Money(line.Price), priceX),
            };
            if (style != 2 && hasTax)
                cells.Add(($"{(int)(taxRate * 100)}%", taxX));
            cells.Add((Money(lineTotal), totalX));
            ocr.Cells(cells.ToArray());
        }
    }

    private const string OcrContentType = "application/vnd.idp.ocr+json";

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Apply OCR-style character confusions (O/0, l/1, rn/m) to a value.</summary>
    private static string OcrConfuse(string text) =>
        text.Replace("o", "0").Replace("l", "1").Replace("rn", "m");

    private static string Corrupt(string taxId) =>
        taxId.Length < 3 ? taxId : taxId[..^1] + (taxId[^1] == 'Z' ? 'Y' : (char)(taxId[^1] + 1));

    /// <summary>Minimal word-box layout builder for the synthetic OCR format.</summary>
    private sealed class Ocr
    {
        private readonly List<Dictionary<string, object>> _words = new();
        private double _y = 40;

        public void Title(string text) { Word(text, 420, _y); _y += 34; }

        public void Free(string text, double x) { Word(text, x, _y); _y += 26; }

        public void Blank() => _y += 14;

        public void Labeled(string label, string value, double valueX = 260)
        {
            double x = 40;
            foreach (var w in label.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Word(w, x, _y);
                x += w.Length * 7 + 7;
            }
            Word(value, valueX, _y);
            _y += 26;
        }

        public void Cells(params (string text, double x)[] cells)
        {
            foreach (var c in cells) Word(c.text, c.x, _y);
            _y += 26;
        }

        private void Word(string text, double x, double y) =>
            _words.Add(new Dictionary<string, object>
            {
                ["text"] = text,
                ["x"] = x,
                ["y"] = y,
                ["w"] = Math.Max(text.Length * 7.0, 14.0),
                ["h"] = 18.0,
            });

        public byte[] ToBytes()
        {
            var doc = new
            {
                pages = new[]
                {
                    new { page = 1, width = 1000.0, height = _y + 40, words = _words },
                },
            };
            return JsonSerializer.SerializeToUtf8Bytes(doc);
        }
    }
}
