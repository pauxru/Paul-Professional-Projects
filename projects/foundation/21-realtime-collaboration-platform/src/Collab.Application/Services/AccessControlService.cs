using Collab.Application.Abstractions;
using Collab.Domain.Authorization;

namespace Collab.Application.Services;

/// <summary>Resolves workspace membership and applies <see cref="RolePolicy"/> — the one authorizer.</summary>
public sealed class AccessControlService(IWorkspaceRepository workspaces, IDocumentRepository documents) : IAccessControl
{
    public async Task<AccessDecision> ForDocumentAsync(Guid userId, Guid documentId, Capability capability, CancellationToken ct = default)
    {
        var document = await documents.GetAsync(documentId, ct);
        if (document is null) return AccessDecision.Deny("Document not found.");
        return await ForWorkspaceAsync(userId, document.WorkspaceId, capability, ct);
    }

    public async Task<AccessDecision> ForWorkspaceAsync(Guid userId, Guid workspaceId, Capability capability, CancellationToken ct = default)
    {
        var member = await workspaces.GetMemberAsync(workspaceId, userId, ct);
        if (member is null) return AccessDecision.Deny("Caller is not a member of the workspace.");
        return RolePolicy.Allows(member.Role, capability)
            ? AccessDecision.Allow(member.Role)
            : AccessDecision.Deny($"Role '{member.Role}' lacks capability '{capability}'.");
    }
}
