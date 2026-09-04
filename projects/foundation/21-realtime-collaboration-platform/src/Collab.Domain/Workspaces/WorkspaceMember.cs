using Collab.Domain.Abstractions;
using Collab.Domain.Authorization;

namespace Collab.Domain.Workspaces;

/// <summary>Membership of a <see cref="User"/> in a <see cref="Workspace"/> with a role.</summary>
public sealed class WorkspaceMember
{
    private WorkspaceMember() { }

    public WorkspaceMember(Guid workspaceId, Guid userId, WorkspaceRole role, IClock clock, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        WorkspaceId = workspaceId;
        UserId = userId;
        Role = role;
        CreatedAt = UpdatedAt = clock.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid UserId { get; private set; }
    public WorkspaceRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void ChangeRole(WorkspaceRole role, IClock clock)
    {
        Role = role;
        UpdatedAt = clock.UtcNow;
    }
}
