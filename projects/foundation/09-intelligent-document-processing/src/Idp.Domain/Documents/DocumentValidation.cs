namespace Idp.Domain.Documents;

/// <summary>The outcome of one deterministic validation rule against a document.</summary>
public sealed class DocumentValidation
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public string RuleName { get; private set; } = default!;
    public ValidationOutcome Outcome { get; private set; }
    public string Message { get; private set; } = default!;

    /// <summary>Comma-separated field keys implicated by this rule (for review highlighting).</summary>
    public string ImplicatedFields { get; private set; } = string.Empty;

    private DocumentValidation() { }

    public DocumentValidation(
        string ruleName,
        ValidationOutcome outcome,
        string message,
        IEnumerable<string>? implicatedFields = null)
    {
        Id = Guid.NewGuid();
        RuleName = ruleName;
        Outcome = outcome;
        Message = message;
        ImplicatedFields = implicatedFields is null
            ? string.Empty
            : string.Join(",", implicatedFields);
    }

    public IReadOnlyList<string> ImplicatedFieldList =>
        string.IsNullOrEmpty(ImplicatedFields)
            ? Array.Empty<string>()
            : ImplicatedFields.Split(',', StringSplitOptions.RemoveEmptyEntries);

    internal void AttachTo(Guid documentId) => DocumentId = documentId;
}
