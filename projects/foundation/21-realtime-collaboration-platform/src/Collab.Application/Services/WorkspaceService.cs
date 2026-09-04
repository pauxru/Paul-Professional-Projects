using Collab.Application.Abstractions;
using Collab.Application.Contracts;
using Collab.Domain.Abstractions;
using Collab.Domain.Authorization;
using Collab.Domain.Workspaces;

namespace Collab.Application.Services;

/// <summary>Workspace and membership management, gated by <see cref="IAccessControl"/>.</summary>
public sealed class WorkspaceService(
    IWorkspaceRepository workspaces,
    IUserRepository users,
    IAccessControl access,
    IAuditLog audit,
    IClock clock,
    IUnitOfWork uow)
{
    public async Task<WorkspaceDto> CreateAsync(Guid actorUserId, CreateWorkspaceRequest request, CancellationToken ct = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 200)
            throw new ValidationAppException(nameof(request.Name), "Name must be between 1 and 200 characters.");

        var workspace = new Workspace(name, clock);
        workspaces.Add(workspace);
        workspaces.AddMember(new WorkspaceMember(workspace.Id, actorUserId, WorkspaceRole.Owner, clock));
        audit.Record("workspace.created", "workspace", workspace.Id.ToString(), actorUserId, workspace.Id);
        await uow.SaveChangesAsync(ct);
        return Map(workspace);
    }

    public async Task<IReadOnlyList<WorkspaceDto>> ListForUserAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var list = await workspaces.ListForUserAsync(actorUserId, ct);
        return list.Select(Map).ToArray();
    }

    public async Task<WorkspaceDto> GetAsync(Guid actorUserId, Guid workspaceId, CancellationToken ct = default)
    {
        await Authorize(actorUserId, workspaceId, Capability.View, ct);
        var workspace = await workspaces.GetAsync(workspaceId, ct)
            ?? throw new NotFoundException("Workspace not found.");
        return Map(workspace);
    }

    public async Task<IReadOnlyList<MemberDto>> ListMembersAsync(Guid actorUserId, Guid workspaceId, CancellationToken ct = default)
    {
        await Authorize(actorUserId, workspaceId, Capability.View, ct);
        var members = await workspaces.ListMembersAsync(workspaceId, ct);
        var result = new List<MemberDto>(members.Count);
        foreach (var member in members)
        {
            var user = await users.GetAsync(member.UserId, ct);
            result.Add(new MemberDto(member.Id, member.UserId, user?.DisplayName ?? "(unknown)", member.Role.ToString()));
        }
        return result;
    }

    public async Task<MemberDto> AddMemberAsync(Guid actorUserId, Guid workspaceId, AddMemberRequest request, CancellationToken ct = default)
    {
        await Authorize(actorUserId, workspaceId, Capability.Manage, ct);
        var role = ParseRole(request.Role);

        if (await workspaces.GetAsync(workspaceId, ct) is null)
            throw new NotFoundException("Workspace not found.");
        if (!await users.ExistsAsync(request.UserId, ct))
            throw new NotFoundException("Target user not found.");

        var existing = await workspaces.GetMemberAsync(workspaceId, request.UserId, ct);
        WorkspaceMember member;
        if (existing is not null)
        {
            existing.ChangeRole(role, clock);
            member = existing;
            audit.Record("workspace.member_role_changed", "workspace", workspaceId.ToString(), actorUserId, workspaceId,
                details: $"user={request.UserId};role={role}");
        }
        else
        {
            member = new WorkspaceMember(workspaceId, request.UserId, role, clock);
            workspaces.AddMember(member);
            audit.Record("workspace.member_added", "workspace", workspaceId.ToString(), actorUserId, workspaceId,
                details: $"user={request.UserId};role={role}");
        }

        await uow.SaveChangesAsync(ct);
        var user = await users.GetAsync(request.UserId, ct);
        return new MemberDto(member.Id, member.UserId, user?.DisplayName ?? "(unknown)", member.Role.ToString());
    }

    public async Task<MemberDto> ChangeRoleAsync(Guid actorUserId, Guid workspaceId, Guid targetUserId, ChangeRoleRequest request, CancellationToken ct = default)
    {
        await Authorize(actorUserId, workspaceId, Capability.Manage, ct);
        var role = ParseRole(request.Role);
        var member = await workspaces.GetMemberAsync(workspaceId, targetUserId, ct)
            ?? throw new NotFoundException("Membership not found.");

        member.ChangeRole(role, clock);
        audit.Record("workspace.member_role_changed", "workspace", workspaceId.ToString(), actorUserId, workspaceId,
            details: $"user={targetUserId};role={role}");
        await uow.SaveChangesAsync(ct);

        var user = await users.GetAsync(targetUserId, ct);
        return new MemberDto(member.Id, member.UserId, user?.DisplayName ?? "(unknown)", member.Role.ToString());
    }

    private async Task Authorize(Guid userId, Guid workspaceId, Capability capability, CancellationToken ct)
    {
        var decision = await access.ForWorkspaceAsync(userId, workspaceId, capability, ct);
        if (!decision.Allowed) throw new ForbiddenException(decision.Reason);
    }

    private static WorkspaceRole ParseRole(string role) =>
        Enum.TryParse<WorkspaceRole>(role, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ValidationAppException("role", $"Unknown role '{role}'. Expected Owner, Editor, Commenter or Viewer.");

    private static WorkspaceDto Map(Workspace w) => new(w.Id, w.Name, w.CreatedAt);
}
