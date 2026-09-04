using Idp.Application.Abstractions;
using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Domain.Documents;
using Idp.Domain.Suppliers;

namespace Idp.UnitTests;

/// <summary>A deterministic clock for tests.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTime utcNow) => UtcNow = utcNow;
    public DateTime UtcNow { get; set; }
}

/// <summary>
/// Builds synthetic OCR content (word boxes on rows) so tests can exercise the spatial extractor and
/// table detector without files. Each <see cref="Row"/> call places words on a new horizontal line.
/// </summary>
public sealed class OcrBuilder
{
    private readonly List<WordBox> _words = new();
    private double _y = 40;

    public OcrBuilder Row(params (string text, double x)[] cells)
    {
        foreach (var (text, x) in cells)
            _words.Add(new WordBox(text, x, _y, Math.Max(10, text.Length * 7), 16, 1));
        _y += 26;
        return this;
    }

    /// <summary>A labelled field: label word(s) at x=40, value at x=260 on the same row.</summary>
    public OcrBuilder Labeled(string label, string value)
    {
        var cells = new List<(string, double)>();
        var x = 40.0;
        foreach (var token in label.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cells.Add((token, x));
            x += Math.Max(30, token.Length * 8);
        }
        cells.Add((value, 260));
        return Row(cells.ToArray());
    }

    public DocumentContent Build(
        string fileName = "doc.ocr.json", string contentType = "application/vnd.idp.ocr+json")
    {
        var rawText = string.Join("\n",
            _words.GroupBy(w => w.Y).OrderBy(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.X).Select(w => w.Text))));
        var page = new OcrPage(1, 800, 1200, _words);
        return new DocumentContent(fileName, contentType, rawText, new[] { page });
    }
}

/// <summary>Fluent builder for a <see cref="Document"/> driven through the real domain state machine.</summary>
public sealed class DocBuilder
{
    private readonly DocumentType _type;
    private readonly List<ExtractedField> _fields = new();
    private readonly List<LineItem> _lines = new();
    private string? _currency;
    private decimal? _value;
    private Guid? _supplierId;
    private string? _supplierNameRaw;
    private static readonly DateTime Now = new(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public DocBuilder(DocumentType type) => _type = type;

    public DocBuilder Field(
        string key, string? value, double confidence = 0.95,
        ExtractionStrategy strategy = ExtractionStrategy.Anchor)
    {
        _fields.Add(new ExtractedField(
            key, value, value, confidence, strategy, DocumentSchemas.IsRequired(_type, key),
            sourceText: value));
        return this;
    }

    public DocBuilder Line(
        int lineNumber, string description, decimal qty, decimal unitPrice,
        decimal? lineTotal = null, decimal? taxRate = null)
    {
        _lines.Add(new LineItem(
            lineNumber, description, qty, unitPrice,
            lineTotal ?? decimal.Round(qty * unitPrice, 2), taxRate, 0.9));
        return this;
    }

    public DocBuilder Currency(string currency) { _currency = currency; return this; }
    public DocBuilder Value(decimal value) { _value = value; return this; }

    public DocBuilder Supplier(Guid id, string nameRaw)
    {
        _supplierId = id;
        _supplierNameRaw = nameRaw;
        return this;
    }

    /// <summary>Build a document in the Extracted state (ready for validation).</summary>
    public Document Build()
    {
        var doc = Document.Receive(
            "doc.ocr.json", "application/vnd.idp.ocr+json", "hash-" + Guid.NewGuid().ToString("N"),
            "key-" + Guid.NewGuid().ToString("N"), 100, "corr-" + Guid.NewGuid().ToString("N"), Now);
        doc.ApplyClassification(_type, 1.0, "test", Now);
        if (_supplierId is { } sid) doc.AssignSupplier(sid, _supplierNameRaw);
        doc.ApplyExtraction(_fields, _lines, _currency, _value ?? _value, Now);
        return doc;
    }

    /// <summary>Build and route the document (Validated -> AutoApproved/InReview/Rejected).</summary>
    public Document BuildRouted(RoutingDecision decision, double confidence = 0.9)
    {
        var doc = Build();
        doc.ApplyValidation(Array.Empty<DocumentValidation>(), Now);
        doc.ApplyRouting(decision, confidence, Now);
        return doc;
    }

    public static Supplier MakeSupplier(
        string name, string? taxId = null, string? currency = "KES", params string[] aliases)
    {
        return new Supplier(name, taxId, currency, Now, aliases, "ACCT-1");
    }
}
