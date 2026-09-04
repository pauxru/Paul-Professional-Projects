namespace Idp.Domain.Suppliers;

/// <summary>
/// A learned extraction hint for a supplier. When a reviewer corrects a field, the system locates
/// the corrected value in the document and records the label word immediately to its left/above as
/// an <see cref="AnchorText"/>. The extractor consults these hints first for that supplier, so the
/// same supplier's next document extracts the field correctly. This is the feedback loop.
/// </summary>
public sealed class SupplierHint
{
    public Guid Id { get; private set; }
    public Guid SupplierId { get; private set; }
    public string FieldKey { get; private set; } = default!;
    public string AnchorText { get; private set; } = default!;
    public int TimesReinforced { get; private set; }
    public Guid LearnedFromDocumentId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private SupplierHint() { }

    public SupplierHint(
        Guid supplierId, string fieldKey, string anchorText, Guid learnedFromDocumentId,
        DateTime nowUtc)
    {
        Id = Guid.NewGuid();
        SupplierId = supplierId;
        FieldKey = fieldKey;
        AnchorText = anchorText;
        LearnedFromDocumentId = learnedFromDocumentId;
        TimesReinforced = 1;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void Reinforce(Guid documentId, DateTime nowUtc)
    {
        TimesReinforced += 1;
        LearnedFromDocumentId = documentId;
        UpdatedAtUtc = nowUtc;
    }
}
