namespace Contoso.Storefront.Domain;

public sealed class WorkerCheckpoint
{
    private WorkerCheckpoint()
    {
    }

    public WorkerCheckpoint(string workerName, Guid lastMessageId, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(workerName))
        {
            throw new ArgumentException("Worker name is required.", nameof(workerName));
        }

        WorkerName = workerName.Trim();
        LastMessageId = lastMessageId;
        UpdatedAt = updatedAt;
    }

    public string WorkerName { get; private set; } = string.Empty;
    public Guid LastMessageId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Advance(Guid lastMessageId, DateTimeOffset updatedAt)
    {
        LastMessageId = lastMessageId;
        UpdatedAt = updatedAt;
    }
}
