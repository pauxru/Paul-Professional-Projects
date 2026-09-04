namespace FraudPipeline.Domain.Entities;

public enum ListType
{
    Allow = 1,
    Deny = 2
}

public enum ListSubject
{
    Card = 1,
    Customer = 2,
    Device = 3,
    Ip = 4,
    Merchant = 5,
    Country = 6
}

/// <summary>
/// Allow-list / deny-list entry. Allow-lists override deny-lists.
/// </summary>
public sealed class ListEntry
{
    public Guid Id { get; private set; }
    public ListType Type { get; private set; }
    public ListSubject Subject { get; private set; }
    public string Value { get; private set; }
    public string Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    private ListEntry() { Value = Reason = ""; }

    public ListEntry(Guid id, ListType type, ListSubject subject, string value, string reason, DateTimeOffset createdAt, DateTimeOffset? expiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(nameof(value));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException(nameof(reason));
        Id = id;
        Type = type;
        Subject = subject;
        Value = value.Trim();
        Reason = reason;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}
