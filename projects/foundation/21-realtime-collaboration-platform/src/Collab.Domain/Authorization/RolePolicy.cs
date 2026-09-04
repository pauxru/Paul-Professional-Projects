namespace Collab.Domain.Authorization;

/// <summary>A role a user holds within a workspace. Order matters only for display.</summary>
public enum WorkspaceRole
{
    Owner = 0,
    Editor = 1,
    Commenter = 2,
    Viewer = 3
}

/// <summary>A discrete capability that authorization policies grant or deny.</summary>
public enum Capability
{
    /// <summary>Read the document, its history and presence.</summary>
    View = 0,
    /// <summary>Add and resolve comments.</summary>
    Comment = 1,
    /// <summary>Submit editing operations that mutate document content.</summary>
    Edit = 2,
    /// <summary>Manage workspace membership and roles.</summary>
    Manage = 3
}

/// <summary>
/// The single source of truth mapping roles to capabilities. Authorization everywhere (REST and
/// the SignalR hub) funnels through <see cref="Allows"/> so there is exactly one place that decides
/// what each role can do.
/// </summary>
public static class RolePolicy
{
    private static readonly IReadOnlyDictionary<WorkspaceRole, HashSet<Capability>> Map =
        new Dictionary<WorkspaceRole, HashSet<Capability>>
        {
            [WorkspaceRole.Owner] = [Capability.View, Capability.Comment, Capability.Edit, Capability.Manage],
            [WorkspaceRole.Editor] = [Capability.View, Capability.Comment, Capability.Edit],
            [WorkspaceRole.Commenter] = [Capability.View, Capability.Comment],
            [WorkspaceRole.Viewer] = [Capability.View]
        };

    public static bool Allows(WorkspaceRole role, Capability capability) =>
        Map.TryGetValue(role, out var caps) && caps.Contains(capability);

    public static IReadOnlyCollection<Capability> CapabilitiesOf(WorkspaceRole role) =>
        Map.TryGetValue(role, out var caps) ? caps : [];
}
