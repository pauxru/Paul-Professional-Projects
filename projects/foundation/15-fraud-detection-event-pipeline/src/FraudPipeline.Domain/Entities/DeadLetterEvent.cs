namespace FraudPipeline.Domain.Entities;

/// <summary>
/// A malformed or unroutable event that could not be processed. Persisted
/// so it can be inspected and (potentially) replayed after fix-forward.
/// </summary>
public sealed class DeadLetterEvent
{
    public Guid Id { get; private set; }
    public string Source { get; private set; }
    public string RawPayload { get; private set; }
    public string Reason { get; private set; }
    public string ExceptionType { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }

    private DeadLetterEvent() { Source = RawPayload = Reason = ExceptionType = ""; }

    public DeadLetterEvent(Guid id, string source, string rawPayload, string reason, string exceptionType, DateTimeOffset receivedAt)
    {
        Id = id;
        Source = source;
        RawPayload = rawPayload;
        Reason = reason;
        ExceptionType = exceptionType;
        ReceivedAt = receivedAt;
    }
}
