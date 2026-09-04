namespace Contoso.Payments.Domain.Common;

/// <summary>Base type for immutable domain events raised by aggregates.</summary>
public abstract record DomainEvent(Guid EventId, DateTimeOffset OccurredAtUtc)
{
    protected DomainEvent() : this(Guid.NewGuid(), DateTimeOffset.UtcNow) { }
}
