namespace Idp.Domain.Review;

/// <summary>Immutable record of a human correction, captured for audit and the feedback loop.</summary>
public sealed class Correction
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public string FieldKey { get; private set; } = default!;
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public string Reason { get; private set; } = default!;
    public string Reviewer { get; private set; } = default!;
    public DateTime CreatedAtUtc { get; private set; }

    private Correction() { }

    public Correction(
        Guid documentId, string fieldKey, string? oldValue, string? newValue, string reason,
        string reviewer, DateTime nowUtc)
    {
        Id = Guid.NewGuid();
        DocumentId = documentId;
        FieldKey = fieldKey;
        OldValue = oldValue;
        NewValue = newValue;
        Reason = reason;
        Reviewer = reviewer;
        CreatedAtUtc = nowUtc;
    }
}
