using Collab.Application.Abstractions;
using Collab.Domain.Abstractions;
using Collab.Domain.Audit;

namespace Collab.Application.Services;

/// <summary>Adds append-only audit records to the current unit of work (persisted on SaveChanges).</summary>
public sealed class AuditService(IAuditRepository repository, IClock clock) : IAuditLog
{
    public void Record(
        string action,
        string resourceType,
        string resourceId,
        Guid? actorUserId = null,
        Guid? workspaceId = null,
        Guid? documentId = null,
        string? details = null,
        string? correlationId = null)
    {
        repository.Add(new AuditRecord(
            action, resourceType, resourceId, clock,
            actorUserId, workspaceId, documentId, details, correlationId));
    }
}
