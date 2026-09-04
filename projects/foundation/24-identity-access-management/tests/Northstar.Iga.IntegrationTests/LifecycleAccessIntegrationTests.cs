using Microsoft.Extensions.DependencyInjection;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;
using Northstar.Iga.Infrastructure;

namespace Northstar.Iga.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class LifecycleAccessIntegrationTests(ApiFactory factory)
{
    private const string Actor = "integration-test";
    private const string Correlation = "integration-correlation";

    [Fact]
    public async Task RoleHierarchy_DeepGrant_ProducesTransitiveDerivationAndRejectsCycle()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db);
        var bottom = await service.CreateRoleAsync(
            new("bottom", "Bottom", "Owns permission.", false, null), Actor, Correlation, default);
        var middle = await service.CreateRoleAsync(
            new("middle", "Middle", "Inherits bottom.", false, null), Actor, Correlation, default);
        var top = await service.CreateRoleAsync(
            new("top", "Top", "Inherits middle.", false, null), Actor, Correlation, default);
        await service.AddRoleEntitlementAsync(bottom.Id, data.Medium.Id, Actor, Correlation, default);
        await service.AddRoleInheritanceAsync(middle.Id, bottom.Id, Actor, Correlation, default);
        await service.AddRoleInheritanceAsync(top.Id, middle.Id, Actor, Correlation, default);
        await service.GrantRoleAsync(data.User.Id, top.Id, GrantSource.Direct, "Test grant", null, Actor, Correlation, default);

        var profile = await service.GetUserAccessProfileAsync(data.User.Id, default);
        var access = Assert.Single(profile.Access);
        Assert.Equal(data.Medium.Id, access.EntitlementId);
        Assert.Contains(access.DerivationPaths, path =>
            path.SequenceEqual(["direct-role", "role:top", "role:middle", "role:bottom", "entitlement:edit-vendors"]));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.AddRoleInheritanceAsync(bottom.Id, top.Id, Actor, Correlation, default));
    }

    [Fact]
    public async Task DynamicGroup_MoverAttributeChange_AddsThenRemovesMembership()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db);
        var group = await service.CreateGroupAsync(
            new("tech-employees", "Technology Employees", "Dynamic technology group.", GroupType.Dynamic,
                "department == Technology && employmentType == Employee"),
            Actor, Correlation, default);

        await service.ProcessMoverAsync(data.User.Id,
            new("Technology", "Engineer", data.Manager.Id, "Nairobi", "CC-TECH", EmploymentType.Employee, 3, 7),
            Actor, Correlation, default);
        Assert.True(db.GroupMembers.Any(x => x.GroupId == group.Id && x.UserId == data.User.Id &&
                                             x.Source == GroupMembershipSource.Dynamic));

        await service.ProcessMoverAsync(data.User.Id,
            new("Finance", "Analyst", data.Manager.Id, "Nairobi", "CC-FIN", EmploymentType.Employee, 3, 7),
            Actor, Correlation, default);
        Assert.False(db.GroupMembers.Any(x => x.GroupId == group.Id && x.UserId == data.User.Id));
    }

    [Fact]
    public async Task DerivationReport_DynamicGroupAndNestedRoles_ExplainsCompletePath()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db);
        var leaf = await service.CreateRoleAsync(
            new("leaf", "Leaf", "Leaf role.", false, null), Actor, Correlation, default);
        var middle = await service.CreateRoleAsync(
            new("nested-middle", "Nested Middle", "Middle role.", false, null), Actor, Correlation, default);
        var top = await service.CreateRoleAsync(
            new("nested-top", "Nested Top", "Top role.", false, null), Actor, Correlation, default);
        await service.AddRoleEntitlementAsync(leaf.Id, data.Medium.Id, Actor, Correlation, default);
        await service.AddRoleInheritanceAsync(middle.Id, leaf.Id, Actor, Correlation, default);
        await service.AddRoleInheritanceAsync(top.Id, middle.Id, Actor, Correlation, default);
        var group = await service.CreateGroupAsync(
            new("finance-dynamic", "Finance Dynamic", "Finance rule.", GroupType.Dynamic,
                "department == Finance && clearance >= 3"),
            Actor, Correlation, default);
        await service.AddGroupRoleAsync(group.Id, top.Id, Actor, Correlation, default);
        await service.ReevaluateDynamicGroupsAsync(data.User.Id, Actor, Correlation, default);

        var profile = await service.GetUserAccessProfileAsync(data.User.Id, default);
        var access = Assert.Single(profile.Access);
        var path = Assert.Single(access.DerivationPaths);
        Assert.Equal(
            ["dynamic-group:finance-dynamic", "group-role", "role:nested-top", "role:nested-middle", "role:leaf", "entitlement:edit-vendors"],
            path);
    }

    [Fact]
    public async Task Joiner_FinanceIdentity_ActivatesAndGrantsBirthrightAccessWithAuditedSteps()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db, IdentityStatus.Pending);
        var role = await service.CreateRoleAsync(
            new("finance-birthright", "Finance Birthright", "Finance joiner access.", true,
                "department == Finance && employmentType == Employee"),
            Actor, Correlation, default);
        await service.AddRoleEntitlementAsync(role.Id, data.Low.Id, Actor, Correlation, default);

        var workflow = await service.ProcessJoinerAsync(data.User.Id, Actor, Correlation, default);

        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        Assert.Equal(IdentityStatus.Active, db.Users.Single(x => x.Id == data.User.Id).Status);
        Assert.True(db.UserRoleGrants.Any(x => x.UserId == data.User.Id && x.RoleId == role.Id &&
                                               x.Source == GrantSource.Birthright));
        var steps = db.LifecycleWorkflowSteps.Where(x => x.WorkflowId == workflow.Id).OrderBy(x => x.Order).ToArray();
        Assert.Equal(4, steps.Length);
        Assert.All(steps, step =>
        {
            Assert.Equal(WorkflowStepStatus.Succeeded, step.Status);
            Assert.Equal(1, step.Attempts);
        });
        Assert.True(db.AuditRecords.Count(x => x.ResourceId == workflow.Id.ToString()) >= 5);
    }

    [Fact]
    public async Task Mover_DepartmentChange_GrantsNewBirthrightAndSchedulesOldAccessReview()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db);
        var financeRole = await service.CreateRoleAsync(
            new("finance-old", "Finance Old", "Finance role.", true, "department == Finance"),
            Actor, Correlation, default);
        var technologyRole = await service.CreateRoleAsync(
            new("technology-new", "Technology New", "Technology role.", true, "department == Technology"),
            Actor, Correlation, default);
        await service.AddRoleEntitlementAsync(financeRole.Id, data.Medium.Id, Actor, Correlation, default);
        await service.AddRoleEntitlementAsync(technologyRole.Id, data.Low.Id, Actor, Correlation, default);
        await service.GrantRoleAsync(
            data.User.Id, financeRole.Id, GrantSource.Birthright, "Existing birthright", null, Actor, Correlation, default);

        await service.ProcessMoverAsync(data.User.Id,
            new("Technology", "Engineer", data.Manager.Id, "Nairobi", "CC-TECH", EmploymentType.Employee, 4, 5),
            Actor, Correlation, default);

        var oldGrant = db.UserRoleGrants.Single(x => x.UserId == data.User.Id && x.RoleId == financeRole.Id);
        Assert.Equal(factory.Clock.UtcNow.AddDays(5), oldGrant.ExpiresAt);
        Assert.True(db.UserRoleGrants.Any(x => x.UserId == data.User.Id && x.RoleId == technologyRole.Id));
        var campaign = Assert.Single(db.Campaigns.Where(x => x.ScopeType == "mover"));
        Assert.True(db.CertificationItems.Any(x => x.CampaignId == campaign.Id && x.EntitlementId == data.Medium.Id));
    }

    [Fact]
    public async Task Mover_ManagerChange_RecertifiesExistingAccessEvenWhenDepartmentIsUnchanged()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db);
        await service.GrantEntitlementAsync(
            data.User.Id, data.Medium.Id, GrantSource.Direct, "Existing direct access", null, Actor, Correlation, default);

        await service.ProcessMoverAsync(data.User.Id,
            new("Finance", "Analyst", data.OwnerTwo.Id, "Nairobi", "CC-FIN", EmploymentType.Employee, 3, 4),
            Actor, Correlation, default);

        var campaign = Assert.Single(db.Campaigns, x => x.ScopeType == "mover");
        var item = Assert.Single(db.CertificationItems, x => x.CampaignId == campaign.Id);
        Assert.Equal(data.Medium.Id, item.EntitlementId);
        Assert.Equal(data.OwnerTwo.Id, item.ReviewerId);
        Assert.Equal(factory.Clock.UtcNow.AddDays(4), campaign.Deadline);
    }

    [Fact]
    public async Task Leaver_RevokesEveryAccessPathTerminatesSessionsAndChangesDecision()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db);
        var role = await service.CreateRoleAsync(
            new("leaver-role", "Leaver Role", "Role for leaver test.", false, null), Actor, Correlation, default);
        await service.AddRoleEntitlementAsync(role.Id, data.Medium.Id, Actor, Correlation, default);
        await service.GrantRoleAsync(data.User.Id, role.Id, GrantSource.Direct, "Standing role", null, Actor, Correlation, default);
        await service.GrantEntitlementAsync(
            data.User.Id, data.Low.Id, GrantSource.Direct, "Standing grant", null, Actor, Correlation, default);
        await service.CreateElevationAsync(new(
            data.User.Id,
            data.Privileged.Id,
            "Emergency production diagnosis",
            "INC-100",
            factory.Clock.UtcNow,
            factory.Clock.UtcNow.AddHours(1),
            false,
            "recording://INC-100"), Actor, Correlation, default);
        var group = await service.CreateGroupAsync(
            new("leaver-static", "Leaver Static", "Static test group.", GroupType.Static, null),
            Actor, Correlation, default);
        await service.AddGroupMemberAsync(group.Id, data.User.Id, Actor, Correlation, default);
        Assert.True((await service.EvaluateAsync(
            new(data.User.Id, data.Medium.Permission, null, null), Actor, Correlation, default)).Allowed);

        var workflow = await service.ProcessLeaverAsync(data.User.Id, Actor, Correlation, default);
        var decision = await service.EvaluateAsync(
            new(data.User.Id, data.Medium.Permission, null, null), Actor, Correlation, default);

        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var user = db.Users.Single(x => x.Id == data.User.Id);
        Assert.Equal(IdentityStatus.Terminated, user.Status);
        Assert.Equal(factory.Clock.UtcNow, user.SessionsTerminatedAt);
        Assert.All(db.UserRoleGrants.Where(x => x.UserId == data.User.Id), x => Assert.NotNull(x.RevokedAt));
        Assert.All(db.UserEntitlementGrants.Where(x => x.UserId == data.User.Id), x => Assert.NotNull(x.RevokedAt));
        Assert.All(db.Elevations.Where(x => x.UserId == data.User.Id), x => Assert.Equal(ElevationStatus.Revoked, x.Status));
        Assert.False(db.GroupMembers.Any(x => x.UserId == data.User.Id));
        Assert.False(decision.Allowed);
        Assert.Contains("identity status", decision.Explanation);
    }

    [Fact]
    public async Task Authorization_DenyPolicyWinsAndExplanationNamesDecisivePolicy()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db);
        await service.GrantEntitlementAsync(
            data.User.Id, data.Medium.Id, GrantSource.Direct, "Role substitute", null, Actor, Correlation, default);
        await service.CreatePolicyAsync(new(
            null, "Finance allow", "Allows Finance.", PolicyEffect.Allow, data.Medium.Permission, 100,
            """{"subject":{"department":"Finance"}}"""), Actor, Correlation, default);
        var deny = await service.CreatePolicyAsync(new(
            null, "Untrusted device deny", "Blocks untrusted devices.", PolicyEffect.Deny, data.Medium.Permission, 1,
            """{"environment":{"deviceTrust":"Untrusted"}}"""), Actor, Correlation, default);

        var decision = await service.EvaluateAsync(new(
            data.User.Id,
            data.Medium.Permission,
            new Dictionary<string, object?> { ["classification"] = "Internal" },
            new Dictionary<string, object?> { ["deviceTrust"] = "Untrusted", ["networkZone"] = "Corporate", ["mfaLevel"] = 3 }),
            Actor, Correlation, default);

        Assert.False(decision.Allowed);
        Assert.Equal(deny.Id, decision.DecisivePolicyId);
        Assert.Contains("Untrusted device deny", decision.Explanation);
        Assert.Equal(2, decision.PoliciesEvaluated.Count);
        Assert.All(decision.PoliciesEvaluated, trace => Assert.NotEmpty(trace.Reasons));
    }

    [Fact]
    public async Task PolicySimulation_ProposedDepartmentAllow_ReportsGainWithoutPersistingPolicy()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IIgaService>();
        var data = await TestData.SeedAsync(db, IdentityStatus.Active, "Research");
        var beforeCount = db.Policies.Count();

        var result = await service.SimulatePolicyAsync(new(
            new(null, "Research what-if", "Proposed access.", PolicyEffect.Allow, data.Medium.Permission, 500,
                """{"subject":{"department":"Research"}}"""),
            data.Medium.Permission,
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>()),
            default);

        Assert.Contains(data.User.Id, result.GainedUserIds);
        Assert.Equal(beforeCount, db.Policies.Count());
        Assert.DoesNotContain(data.Manager.Id, result.GainedUserIds);
    }
}
