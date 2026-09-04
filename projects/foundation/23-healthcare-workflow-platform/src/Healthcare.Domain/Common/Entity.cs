namespace Healthcare.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();

    private readonly List<IDomainEvent> _domainEvents = new();

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent evt) => _domainEvents.Add(evt);

    public void ClearDomainEvents() => _domainEvents.Clear();

    public override bool Equals(object? obj) =>
        obj is Entity e && e.GetType() == GetType() && e.Id == Id;

    public override int GetHashCode() => Id.GetHashCode();
}

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
