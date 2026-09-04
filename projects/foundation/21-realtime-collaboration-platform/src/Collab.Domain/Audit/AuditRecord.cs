using Collab.Domain.Abstractions;

namespace Collab.Domain.Audit;

/// <summary>
/// An append-only audit record. Written for every security- or content-significant action
/// (membership and role changes, document lifecycle, version restore, comment resolution). Never
/// updated or deleted by application code.
/// </summary>
public sealed class AuditRecord
{
    private AuditRecord() { }

    public AuditRecord(
        string action,
        string resourceType,
        string resourceId,
        IClock clock,
        Guid? actorUserId = null,
        Guid? workspaceId = null,
        Guid? documentId = null,
        string? details = null,
        string? correlationId = null)
    {
        Id = Guid.NewGuid();
        Action = action;
        ResourceType = resourceType;
        ResourceId = resourceId;
        ActorUserId = actorUserId;
        WorkspaceId = workspaceId;
        DocumentId = documentId;
        Details = details;
        CorrelationId = correlationId;
        CreatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Action { get; private set; } = null!;
    public string ResourceType { get; private set; } = null!;
    public string ResourceId { get; private set; } = null!;
    public Guid? ActorUserId { get; private set; }
    public Guid? WorkspaceId { get; private set; }
    public Guid? DocumentId { get; private set; }
    public string? Details { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
