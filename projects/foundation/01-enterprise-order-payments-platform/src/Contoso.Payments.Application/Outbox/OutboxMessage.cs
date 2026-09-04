namespace Contoso.Payments.Application.Outbox;

/// <summary>
/// A domain event that has been persisted in the same transaction as its aggregate change.
/// A hosted <c>OutboxDispatcher</c> service polls this table and publishes to <see cref="Contoso.Payments.Application.Abstractions.IEventBus"/>
/// with at-least-once semantics.  Retries use exponential backoff with jitter, capped by
/// <see cref="Attempts"/> = MaxAttempts, after which the row is copied to the dead-letter table.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Topic { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool Dispatched { get; set; }
    public DateTimeOffset? DispatchedAtUtc { get; set; }

    /// <summary>Correlation id of the request that produced the event, if any.</summary>
    public string CorrelationId { get; set; } = string.Empty;
}

public sealed class OutboxDeadLetter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OriginalMessageId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTimeOffset DeadLetteredAtUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}
