namespace Contoso.Storefront.Domain;

public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public OutboxMessage(Guid id, string type, string payload, DateTimeOffset occurredAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Message id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Message type is required.", nameof(type));
        }

        Id = id;
        Type = type.Trim();
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public int DeliveryAttempts { get; private set; }
    public string? LastError { get; private set; }

    public void MarkAttempt(string? error)
    {
        DeliveryAttempts++;
        LastError = error;
    }

    public void MarkProcessed(DateTimeOffset processedAt)
    {
        ProcessedAt = processedAt;
        LastError = null;
    }
}
