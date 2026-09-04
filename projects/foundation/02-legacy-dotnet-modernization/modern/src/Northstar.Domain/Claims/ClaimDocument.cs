namespace Northstar.Domain.Claims;

public sealed class ClaimDocument
{
    private ClaimDocument()
    {
    }

    public ClaimDocument(Guid id, Guid claimId, string originalName, string contentType, string storageKey, DateTimeOffset uploadedAt)
    {
        if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("A document name and storage key are required.");
        }

        Id = id;
        ClaimId = claimId;
        OriginalName = originalName;
        ContentType = contentType;
        StorageKey = storageKey;
        UploadedAt = uploadedAt;
    }

    public Guid Id { get; private set; }
    public Guid ClaimId { get; private set; }
    public string OriginalName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public string StorageKey { get; private set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; private set; }
}
