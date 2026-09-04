using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Domain.Documents;
using Idp.Infrastructure.Extraction;

namespace Idp.UnitTests;

/// <summary>
/// Proves the correction feedback loop end-to-end and deterministically: a field that the extractor
/// misses on v1 (because the supplier uses a non-standard label) is learned from a human correction
/// and then extracted correctly on v2 of the same layout — no database, no model retraining.
/// </summary>
public class CorrectionFeedbackTests
{
    private static DocumentContent SupplierLayout() => new OcrBuilder()
        .Labeled("Supplier", "Rift Valley Supplies")
        .Row(("Ref:", 40), ("INV-999", 120))          // non-standard label for the invoice number
        .Labeled("Total", "1000.00")
        .Build();

    [Fact]
    public void Correction_learns_an_anchor_that_fixes_the_next_extraction()
    {
        var extractor = new DeterministicFieldExtractor();
        var content = SupplierLayout();

        // v1: the invoice number is NOT extracted because "Ref:" is not a built-in anchor.
        var v1 = extractor.Extract(DocumentType.Invoice, content, Array.Empty<ExtractionHint>());
        Assert.Null(v1.Fields.FirstOrDefault(f => f.FieldKey == FieldKeys.InvoiceNumber));

        // A reviewer corrects the value; we learn the anchor label to its left.
        var anchor = AnchorLearner.FindAnchor(content, "INV-999");
        Assert.Equal("Ref", anchor, ignoreCase: true);

        // v2: same layout, now with the learned per-supplier hint -> correct extraction.
        var hints = new[] { new ExtractionHint(FieldKeys.InvoiceNumber, anchor!) };
        var v2 = extractor.Extract(DocumentType.Invoice, content, hints);
        var field = v2.Fields.FirstOrDefault(f => f.FieldKey == FieldKeys.InvoiceNumber);
        Assert.NotNull(field);
        Assert.Equal("INV-999", field!.RawValue);
        Assert.Equal(ExtractionStrategy.LearnedAnchor, field.Strategy);
    }

    [Fact]
    public void Supplier_stores_the_learned_hint_for_the_field()
    {
        var supplier = DocBuilder.MakeSupplier("Rift Valley Supplies Ltd");
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        supplier.LearnHint(FieldKeys.InvoiceNumber, "Ref", docId, now);
        var hint = supplier.HintFor(FieldKeys.InvoiceNumber);
        Assert.NotNull(hint);
        Assert.Equal("Ref", hint!.AnchorText);

        // Re-learning the same anchor reinforces rather than duplicating.
        supplier.LearnHint(FieldKeys.InvoiceNumber, "Ref", docId, now.AddMinutes(1));
        Assert.Single(supplier.Hints);
    }
}
