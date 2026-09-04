using Idp.Domain.Documents;

namespace Idp.Application.Extraction;

/// <summary>The result of extracting fields and line items from a document.</summary>
public sealed class ExtractionResult
{
    public IReadOnlyList<ExtractedField> Fields { get; }
    public IReadOnlyList<LineItem> LineItems { get; }
    public string? Currency { get; }
    public decimal? DocumentValue { get; }

    public ExtractionResult(
        IReadOnlyList<ExtractedField> fields,
        IReadOnlyList<LineItem> lineItems,
        string? currency,
        decimal? documentValue)
    {
        Fields = fields;
        LineItems = lineItems;
        Currency = currency;
        DocumentValue = documentValue;
    }
}

/// <summary>A learned per-supplier extraction hint passed to the extractor (feedback loop input).</summary>
public sealed record ExtractionHint(string FieldKey, string AnchorText);

/// <summary>
/// Extracts typed fields from a classified document using anchor/regex/positional strategies and a
/// spatial table detector. Learned per-supplier hints are consulted first so corrections improve the
/// next document from the same supplier.
/// </summary>
public interface IFieldExtractor
{
    ExtractionResult Extract(
        DocumentType type,
        Documents.DocumentContent content,
        IReadOnlyList<ExtractionHint> hints);
}
