namespace Contoso.Payments.Domain.Common;

/// <summary>
/// Base class for aggregate roots.  Aggregates buffer their events; the persistence layer
/// drains and enqueues them into the transactional outbox in the same DbContext transaction.
/// </summary>
public abstract class AggregateRoot
{
    private readonly List<DomainEvent> _events = new();

    public IReadOnlyList<DomainEvent> DequeueEvents()
    {
        var snapshot = _events.ToArray();
        _events.Clear();
        return snapshot;
    }

    protected void Raise(DomainEvent @event) => _events.Add(@event);

    public int Version { get; protected set; }
}
