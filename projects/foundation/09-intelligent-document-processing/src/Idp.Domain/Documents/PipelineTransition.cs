namespace Idp.Domain.Documents;

/// <summary>An audited pipeline state transition (part of the document's immutable history).</summary>
public sealed class PipelineTransition
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public PipelineState FromState { get; private set; }
    public PipelineState ToState { get; private set; }
    public string Reason { get; private set; } = default!;
    public string Actor { get; private set; } = default!;
    public DateTime OccurredAtUtc { get; private set; }

    private PipelineTransition() { }

    public PipelineTransition(
        Guid documentId,
        PipelineState fromState,
        PipelineState toState,
        string reason,
        string actor,
        DateTime occurredAtUtc)
    {
        Id = Guid.NewGuid();
        DocumentId = documentId;
        FromState = fromState;
        ToState = toState;
        Reason = reason;
        Actor = actor;
        OccurredAtUtc = occurredAtUtc;
    }
}
