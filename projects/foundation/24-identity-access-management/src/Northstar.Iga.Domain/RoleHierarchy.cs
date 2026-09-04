namespace Northstar.Iga.Domain;

public sealed record RolePath(IReadOnlyList<Guid> RoleIds);
public sealed record EntitlementPath(Guid EntitlementId, IReadOnlyList<Guid> RoleIds);

public sealed class RoleHierarchyResolver(IEnumerable<RoleInheritance> inheritances)
{
    private readonly IReadOnlyDictionary<Guid, Guid[]> _parents = inheritances
        .GroupBy(x => x.RoleId)
        .ToDictionary(x => x.Key, x => x.Select(y => y.InheritedRoleId).Distinct().ToArray());

    public bool WouldCreateCycle(Guid roleId, Guid inheritedRoleId)
    {
        if (roleId == inheritedRoleId)
        {
            return true;
        }

        return IsReachable(inheritedRoleId, roleId, []);
    }

    public IReadOnlyList<RolePath> GetRolePaths(Guid roleId)
    {
        var paths = new List<RolePath>();
        Visit(roleId, [roleId], new HashSet<Guid> { roleId }, paths);
        return paths;
    }

    public IReadOnlyList<EntitlementPath> ResolveEntitlementPaths(
        Guid roleId,
        IEnumerable<RoleEntitlement> roleEntitlements)
    {
        var byRole = roleEntitlements
            .GroupBy(x => x.RoleId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.EntitlementId).Distinct().ToArray());
        var results = new List<EntitlementPath>();

        foreach (var path in GetRolePaths(roleId))
        {
            var terminalRole = path.RoleIds[^1];
            if (!byRole.TryGetValue(terminalRole, out var entitlementIds))
            {
                continue;
            }

            results.AddRange(entitlementIds.Select(id => new EntitlementPath(id, path.RoleIds)));
        }

        return results;
    }

    private bool IsReachable(Guid start, Guid target, HashSet<Guid> visited)
    {
        if (start == target)
        {
            return true;
        }

        if (!visited.Add(start) || !_parents.TryGetValue(start, out var next))
        {
            return false;
        }

        return next.Any(parent => IsReachable(parent, target, visited));
    }

    private void Visit(Guid current, List<Guid> path, HashSet<Guid> visiting, List<RolePath> paths)
    {
        paths.Add(new RolePath(path.ToArray()));
        if (!_parents.TryGetValue(current, out var parents))
        {
            return;
        }

        foreach (var parent in parents)
        {
            if (!visiting.Add(parent))
            {
                throw new DomainRuleException("The persisted role hierarchy contains a cycle.");
            }

            path.Add(parent);
            Visit(parent, path, visiting, paths);
            path.RemoveAt(path.Count - 1);
            visiting.Remove(parent);
        }
    }
}
