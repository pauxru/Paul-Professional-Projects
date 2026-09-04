using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;
using Northstar.Iga.Infrastructure;

namespace Northstar.Iga.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class GovernanceWorkflowIntegrationTests(ApiFactory factory)
{
    private const string Actor = "integration-test";
    private const string Correlation = "governance-correlation";

    [Fact]
    public async Task SoD_PreventiveControl_BlocksToxicAccessRequest()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, db, data) = await ServicesAsync(scope);
        await service.CreateSoDRuleAsync(new(
            "Vendor/payment conflict", data.ToxicA.Id, data.ToxicB.Id, RiskRating.Critical,
            "Creator and releaser must be different people."), Actor, Correlation, default);
        await service.GrantEntitlementAsync(
            data.User.Id, data.ToxicA.Id, GrantSource.Direct, "Vendor duties", null, Actor, Correlation, default);

        var exception = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAccessRequestAsync(new(
                data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.ToxicB.Id,
                "Need to release supplier payments", 30), Actor, Correlation, default));

        Assert.Contains("Preventive SoD", exception.Message);
        Assert.Empty(db.AccessRequests);
    }

    [Fact]
    public async Task SoD_DetectiveScan_FindsExistingToxicCombination()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var rule = await service.CreateSoDRuleAsync(new(
            "Vendor/payment conflict", data.ToxicA.Id, data.ToxicB.Id, RiskRating.Critical,
            "Creator and releaser must be different people."), Actor, Correlation, default);
        await service.GrantEntitlementAsync(
            data.User.Id, data.ToxicA.Id, GrantSource.Direct, "Vendor duties", null, Actor, Correlation, default);
        await service.GrantEntitlementAsync(
            data.User.Id, data.ToxicB.Id, GrantSource.Direct, "Payment duties", null, Actor, Correlation, default);

        var violation = Assert.Single(await service.ScanSoDAsync(default));

        Assert.Equal(rule.Id, violation.RuleId);
        Assert.Equal(data.User.Id, violation.UserId);
        Assert.False(violation.HasActiveException);
    }

    [Fact]
    public async Task SoD_ApprovedUnexpiredException_AllowsRequest()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var rule = await service.CreateSoDRuleAsync(new(
            "Vendor/payment conflict", data.ToxicA.Id, data.ToxicB.Id, RiskRating.Critical,
            "Creator and releaser must be different people."), Actor, Correlation, default);
        await service.GrantEntitlementAsync(
            data.User.Id, data.ToxicA.Id, GrantSource.Direct, "Vendor duties", null, Actor, Correlation, default);
        await service.CreateSoDExceptionAsync(new(
            rule.Id, data.User.Id, "Chief Risk Officer", "Emergency quarter-end coverage",
            factory.Clock.UtcNow.AddHours(2)), Actor, Correlation, default);

        var request = await service.CreateAccessRequestAsync(new(
            data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.ToxicB.Id,
            "Approved quarter-end payment coverage", 1), Actor, Correlation, default);

        Assert.Equal(AccessRequestStatus.Pending, request.Status);
    }

    [Fact]
    public async Task SoD_ExpiredException_NoLongerAllowsRequest()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var rule = await service.CreateSoDRuleAsync(new(
            "Vendor/payment conflict", data.ToxicA.Id, data.ToxicB.Id, RiskRating.Critical,
            "Creator and releaser must be different people."), Actor, Correlation, default);
        await service.GrantEntitlementAsync(
            data.User.Id, data.ToxicA.Id, GrantSource.Direct, "Vendor duties", null, Actor, Correlation, default);
        await service.CreateSoDExceptionAsync(new(
            rule.Id, data.User.Id, "Chief Risk Officer", "One-hour exception",
            factory.Clock.UtcNow.AddHours(1)), Actor, Correlation, default);
        factory.Clock.Advance(TimeSpan.FromHours(2));

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAccessRequestAsync(new(
                data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.ToxicB.Id,
                "Coverage after the exception expired", 1), Actor, Correlation, default));
    }

    [Fact]
    public async Task ApprovalWorkflow_SequentialManagerThenOwner_FulfillsAfterBoth()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var request = await service.CreateAccessRequestAsync(new(
            data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.Medium.Id,
            "Need vendor editing for assigned duties", 30), Actor, Correlation, default);
        var steps = await service.GetApprovalStepsAsync(request.Id, default);
        var managerStep = Assert.Single(steps, x => x.Stage == 1);
        var ownerStep = Assert.Single(steps, x => x.Stage == 2);

        var afterManager = await service.ApproveRequestAsync(
            request.Id, managerStep.Id, new(data.Manager.Id, "Manager confirms need"), Actor, Correlation, default);
        Assert.Equal(AccessRequestStatus.Pending, afterManager.Status);
        Assert.Equal(2, afterManager.CurrentStage);
        var afterOwner = await service.ApproveRequestAsync(
            request.Id, ownerStep.Id, new(data.OwnerOne.Id, "Owner accepts risk"), Actor, Correlation, default);

        Assert.Equal(AccessRequestStatus.Fulfilled, afterOwner.Status);
        Assert.Contains((await service.GetUserAccessProfileAsync(data.User.Id, default)).Access,
            x => x.EntitlementId == data.Medium.Id);
    }

    [Fact]
    public async Task ApprovalWorkflow_ParallelOwnerStage_WaitsForAllOwners()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, db, data) = await ServicesAsync(scope);
        var otherEntitlement = TestData.Entitlement(
            data.Finance.Id, "review-vendors", "app:finance/vendor:review", data.OwnerTwo.Id, RiskRating.Medium);
        db.Entitlements.Add(otherEntitlement);
        var role = new Role { Key = "parallel-role", Name = "Parallel Role", Description = "Two owners." };
        db.Roles.Add(role);
        db.RoleEntitlements.AddRange(
            new RoleEntitlement { RoleId = role.Id, EntitlementId = data.Medium.Id },
            new RoleEntitlement { RoleId = role.Id, EntitlementId = otherEntitlement.Id });
        await db.SaveChangesAsync();
        var request = await service.CreateAccessRequestAsync(new(
            data.User.Id, data.User.Id, RequestTargetType.Role, role.Id,
            "Need both vendor capabilities for project", 30), Actor, Correlation, default);
        var steps = await service.GetApprovalStepsAsync(request.Id, default);
        await service.ApproveRequestAsync(
            request.Id,
            steps.Single(x => x.Stage == 1).Id,
            new(data.Manager.Id, "Manager approved"),
            Actor, Correlation, default);
        var ownerSteps = (await service.GetApprovalStepsAsync(request.Id, default)).Where(x => x.Stage == 2).ToArray();
        Assert.Equal(2, ownerSteps.Length);

        var first = ownerSteps[0];
        var firstResult = await service.ApproveRequestAsync(
            request.Id, first.Id, new(first.ApproverId, "First owner approved"), Actor, Correlation, default);
        Assert.Equal(AccessRequestStatus.Pending, firstResult.Status);
        var second = ownerSteps[1];
        var final = await service.ApproveRequestAsync(
            request.Id, second.Id, new(second.ApproverId, "Second owner approved"), Actor, Correlation, default);
        Assert.Equal(AccessRequestStatus.Fulfilled, final.Status);
    }

    [Fact]
    public async Task ApprovalWorkflow_Delegation_OnlyDelegateCanApprove()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var request = await service.CreateAccessRequestAsync(new(
            data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.Medium.Id,
            "Need delegated approval path tested", 30), Actor, Correlation, default);
        var managerStep = (await service.GetApprovalStepsAsync(request.Id, default)).Single(x => x.Stage == 1);
        await service.DelegateApprovalAsync(
            request.Id, managerStep.Id, new(data.Manager.Id, data.OwnerTwo.Id), Actor, Correlation, default);

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.ApproveRequestAsync(
                request.Id, managerStep.Id, new(data.Manager.Id, "Old approver"), Actor, Correlation, default));
        var result = await service.ApproveRequestAsync(
            request.Id, managerStep.Id, new(data.OwnerTwo.Id, "Acting delegate"), Actor, Correlation, default);
        Assert.Equal(2, result.CurrentStage);
        Assert.True((await service.GetApprovalStepsAsync(request.Id, default))
            .Single(x => x.Id == managerStep.Id).WasDelegated);
    }

    [Fact]
    public async Task ApprovalWorkflow_SlaBreach_EscalatesPendingStep()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var request = await service.CreateAccessRequestAsync(new(
            data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.Medium.Id,
            "Need an escalation scenario for testing", 30), Actor, Correlation, default);
        factory.Clock.Advance(TimeSpan.FromHours(25));

        var count = await service.EscalateOverdueApprovalsAsync(
            data.Security.Id, Actor, Correlation, default);
        var step = (await service.GetApprovalStepsAsync(request.Id, default)).Single(x => x.Stage == 1);

        Assert.Equal(1, count);
        Assert.Equal(data.Security.Id, step.ApproverId);
        Assert.True(step.WasEscalated);
    }

    [Fact]
    public async Task ApprovalWorkflow_LowRiskItem_AutoApprovesAndFulfills()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);

        var request = await service.CreateAccessRequestAsync(new(
            data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.Low.Id,
            "Need read-only access for routine duties", null), Actor, Correlation, default);

        Assert.Equal(AccessRequestStatus.Fulfilled, request.Status);
        Assert.Empty(await service.GetApprovalStepsAsync(request.Id, default));
        Assert.Contains((await service.GetUserAccessProfileAsync(data.User.Id, default)).Access,
            x => x.EntitlementId == data.Low.Id);
    }

    [Fact]
    public async Task ApprovalWorkflow_Rejection_RequiresAndPersistsReason()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var request = await service.CreateAccessRequestAsync(new(
            data.User.Id, data.User.Id, RequestTargetType.Entitlement, data.Medium.Id,
            "Need access that should be rejected", 30), Actor, Correlation, default);
        var step = (await service.GetApprovalStepsAsync(request.Id, default)).Single(x => x.Stage == 1);

        var rejected = await service.RejectRequestAsync(
            request.Id, step.Id, new(data.Manager.Id, "Business need is not demonstrated"), Actor, Correlation, default);

        Assert.Equal(AccessRequestStatus.Rejected, rejected.Status);
        Assert.Equal("Business need is not demonstrated", rejected.RejectionReason);
    }

    [Fact]
    public async Task JitElevation_GrantChangesDecision_ExpiryRemovesPermissionAndChangesItBack()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var before = await service.EvaluateAsync(
            new(data.User.Id, data.Privileged.Permission, null, null), Actor, Correlation, default);
        var elevation = await service.CreateElevationAsync(new(
            data.User.Id,
            data.Privileged.Id,
            "Investigate production incident with temporary privilege",
            "INC-4242",
            factory.Clock.UtcNow,
            factory.Clock.UtcNow.AddMinutes(30),
            false,
            "recording://INC-4242"), Actor, Correlation, default);
        var during = await service.EvaluateAsync(
            new(data.User.Id, data.Privileged.Permission, null, null), Actor, Correlation, default);
        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        var expired = await service.ExpireElevationsAsync(Actor, Correlation, default);
        var after = await service.EvaluateAsync(
            new(data.User.Id, data.Privileged.Permission, null, null), Actor, Correlation, default);

        Assert.False(before.Allowed);
        Assert.True(during.Allowed);
        Assert.Contains(during.Derivations, x => x.Path[0] == "jit-elevation:INC-4242");
        Assert.Equal(1, expired);
        Assert.False(after.Allowed);
        Assert.Equal(ElevationStatus.Expired,
            (await service.GetElevationsAsync(default)).Single(x => x.Id == elevation.Id).Status);
    }

    [Fact]
    public async Task JitElevation_EarlyRevocation_ImmediatelyRemovesPermission()
    {
        await factory.ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var (service, _, data) = await ServicesAsync(scope);
        var elevation = await service.CreateElevationAsync(new(
            data.User.Id,
            data.Privileged.Id,
            "Temporary break-glass access for diagnostics",
            "INC-5000",
            factory.Clock.UtcNow,
            factory.Clock.UtcNow.AddHours(1),
            false,
            "recording://INC-5000"), Actor, Correlation, default);
        await service.RevokeElevationAsync(
            elevation.Id, "Incident resolved early", Actor, Correlation, default);
        var decision = await service.EvaluateAsync(
            new(data.User.Id, data.Privileged.Permission, null, null), Actor, Correlation, default);
        Assert.False(decision.Allowed);
    }

    private static async Task<(IIgaService Service, IgaDbContext Db, SeedData Data)> ServicesAsync(
        AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<IgaDbContext>();
        var data = await TestData.SeedAsync(db);
        return (scope.ServiceProvider.GetRequiredService<IIgaService>(), db, data);
    }
}
