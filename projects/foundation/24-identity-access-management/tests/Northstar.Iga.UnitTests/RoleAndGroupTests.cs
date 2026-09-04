using Northstar.Iga.Domain;

namespace Northstar.Iga.UnitTests;

public sealed class RoleHierarchyTests
{
    [Fact]
    public void ResolveEntitlementPaths_DeepHierarchy_ResolvesTransitivePermission()
    {
        var roles = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var entitlement = Guid.NewGuid();
        var edges = Enumerable.Range(0, 5)
            .Select(index => new RoleInheritance { RoleId = roles[index], InheritedRoleId = roles[index + 1] })
            .ToArray();

        var paths = new RoleHierarchyResolver(edges).ResolveEntitlementPaths(
            roles[0],
            [new RoleEntitlement { RoleId = roles[5], EntitlementId = entitlement }]);

        var path = Assert.Single(paths);
        Assert.Equal(entitlement, path.EntitlementId);
        Assert.Equal(roles, path.RoleIds);
    }

    [Fact]
    public void WouldCreateCycle_SelfReference_ReturnsTrue()
    {
        var role = Guid.NewGuid();
        Assert.True(new RoleHierarchyResolver([]).WouldCreateCycle(role, role));
    }

    [Fact]
    public void WouldCreateCycle_TransitiveBackEdge_ReturnsTrue()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var resolver = new RoleHierarchyResolver(
        [
            new RoleInheritance { RoleId = a, InheritedRoleId = b },
            new RoleInheritance { RoleId = b, InheritedRoleId = c }
        ]);

        Assert.True(resolver.WouldCreateCycle(c, a));
    }

    [Fact]
    public void ResolveEntitlementPaths_DiamondHierarchy_PreservesBothDerivations()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var entitlement = Guid.NewGuid();
        var resolver = new RoleHierarchyResolver(
        [
            new RoleInheritance { RoleId = a, InheritedRoleId = b },
            new RoleInheritance { RoleId = a, InheritedRoleId = c },
            new RoleInheritance { RoleId = b, InheritedRoleId = d },
            new RoleInheritance { RoleId = c, InheritedRoleId = d }
        ]);

        var paths = resolver.ResolveEntitlementPaths(
            a,
            [new RoleEntitlement { RoleId = d, EntitlementId = entitlement }]);

        Assert.Equal(2, paths.Count);
        Assert.All(paths, path => Assert.Equal(3, path.RoleIds.Count));
    }

    [Fact]
    public void ResolveEntitlementPaths_NoEntitlement_ReturnsEmpty()
    {
        Assert.Empty(new RoleHierarchyResolver([]).ResolveEntitlementPaths(Guid.NewGuid(), []));
    }
}

public sealed class DynamicGroupRuleEvaluatorTests
{
    [Fact]
    public void IsMatch_DepartmentEquality_ReturnsTrue()
    {
        Assert.True(DynamicGroupRuleEvaluator.IsMatch(User(), "department == Finance"));
    }

    [Fact]
    public void IsMatch_MultipleClauses_RequiresAll()
    {
        Assert.True(DynamicGroupRuleEvaluator.IsMatch(
            User(),
            "department == Finance && employmentType == Employee && status == Active"));
    }

    [Fact]
    public void IsMatch_MismatchedClause_ReturnsFalse()
    {
        Assert.False(DynamicGroupRuleEvaluator.IsMatch(User(), "department == Technology"));
    }

    [Fact]
    public void IsMatch_NumericClearanceComparison_Works()
    {
        Assert.True(DynamicGroupRuleEvaluator.IsMatch(User(), "clearance >= 3"));
    }

    [Fact]
    public void IsMatch_ContainsOperator_IsCaseInsensitive()
    {
        Assert.True(DynamicGroupRuleEvaluator.IsMatch(User(), "jobTitle contains analyst"));
    }

    [Fact]
    public void IsMatch_StartsWithOperator_IsCaseInsensitive()
    {
        Assert.True(DynamicGroupRuleEvaluator.IsMatch(User(), "costCentre startsWith cc-fi"));
    }

    [Fact]
    public void IsMatch_InvalidRule_Throws()
    {
        Assert.Throws<DomainRuleException>(() =>
            DynamicGroupRuleEvaluator.IsMatch(User(), "department ~ Finance"));
    }

    [Fact]
    public void IsMatch_UnsupportedAttribute_Throws()
    {
        Assert.Throws<DomainRuleException>(() =>
            DynamicGroupRuleEvaluator.IsMatch(User(), "favouriteColour == Blue"));
    }

    private static UserIdentity User() => new()
    {
        Department = "Finance",
        JobTitle = "Senior Analyst",
        CostCentre = "CC-FIN",
        EmploymentType = EmploymentType.Employee,
        Clearance = 3,
        Status = IdentityStatus.Active
    };
}
