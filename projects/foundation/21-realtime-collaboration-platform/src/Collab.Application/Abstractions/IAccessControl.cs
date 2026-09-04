using Collab.Domain.Authorization;

namespace Collab.Application.Abstractions;

/// <summary>The result of an authorization check, carrying the caller's effective role for auditing.</summary>
public sealed record AccessDecision(bool Allowed, WorkspaceRole? Role, string Reason)
{
    public static AccessDecision Allow(WorkspaceRole role) => new(true, role, string.Empty);
    public static AccessDecision Deny(string reason) => new(false, null, reason);
}

/// <summary>
/// The single choke point for authorization. Resolves a user's workspace membership and applies
/// <see cref="RolePolicy"/> to decide whether a capability is permitted for a document or workspace.
/// Every REST endpoint and every hub method routes through this.
/// </summary>
public interface IAccessControl
{
    Task<AccessDecision> ForDocumentAsync(Guid userId, Guid documentId, Capability capability, CancellationToken ct = default);
    Task<AccessDecision> ForWorkspaceAsync(Guid userId, Guid workspaceId, Capability capability, CancellationToken ct = default);
}
