using FieldOps.Application;
using FieldOps.Domain;

namespace FieldOps.UnitTests;

public sealed class InspectionInvitationAuthorizationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");

    [Fact]
    public void Inspection_BooleanAndNumberAnswers_CalculateWeightedScore()
    {
        var (service, template) = CreateInspection();
        var result = service.Evaluate(template,
        [
            new(template.Items.ElementAt(0).Id, "true"),
            new(template.Items.ElementAt(1).Id, "5")
        ]);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }

    [Fact]
    public void Inspection_MissingRequiredAnswer_IsRejected()
    {
        var (service, template) = CreateInspection();
        Assert.Throws<DomainRuleException>(() => service.Evaluate(template,
        [
            new(template.Items.ElementAt(0).Id, null),
            new(template.Items.ElementAt(1).Id, "5")
        ]));
    }

    [Fact]
    public void Inspection_OutOfRangeNumber_FailsWeightedRule()
    {
        var (service, template) = CreateInspection();
        var result = service.Evaluate(template,
        [
            new(template.Items.ElementAt(0).Id, "true"),
            new(template.Items.ElementAt(1).Id, "50")
        ]);
        Assert.Equal(40, result.Score);
        Assert.False(result.Passed);
    }

    [Fact]
    public void Inspection_UnknownItem_IsRejected()
    {
        var (service, template) = CreateInspection();
        Assert.Throws<DomainRuleException>(() => service.Evaluate(template,
        [
            new(template.Items.ElementAt(0).Id, "true"),
            new(template.Items.ElementAt(1).Id, "5"),
            new(Guid.NewGuid(), "unexpected")
        ]));
    }

    [Fact]
    public void Inspection_PhotoReference_RequiresObjectScheme()
    {
        var tenant = Guid.NewGuid();
        var template = new InspectionTemplate(tenant, "Photo", 100);
        var item = template.AddItem("Evidence", InspectionItemType.PhotoReference, true, 1);
        var service = new InspectionService(
            new FakeTenantContext(tenant),
            new NullInspectionRepository(),
            new FakeClock(Now));
        Assert.False(service.Evaluate(template, [new(item.Id, "https://example.test/file.jpg")]).Passed);
        Assert.True(service.Evaluate(template, [new(item.Id, "obj://tenant/file.jpg")]).Passed);
    }

    [Fact]
    public void Invitation_AfterExpiry_IsRejected()
    {
        const string token = "known-token";
        var invitation = new Invitation(
            Guid.NewGuid(), "invitee@example.test", MemberRole.Technician, token, Now.AddHours(1), Now);
        Assert.Throws<DomainRuleException>(() => invitation.Accept(token, Now.AddHours(2)));
    }

    [Fact]
    public void Invitation_WrongToken_IsRejected()
    {
        var invitation = new Invitation(
            Guid.NewGuid(), "invitee@example.test", MemberRole.Viewer, "correct", Now.AddHours(1), Now);
        Assert.Throws<DomainRuleException>(() => invitation.Accept("wrong", Now.AddMinutes(1)));
    }

    [Fact]
    public void Invitation_ValidToken_CanOnlyBeAcceptedOnce()
    {
        const string token = "correct";
        var invitation = new Invitation(
            Guid.NewGuid(), "invitee@example.test", MemberRole.Viewer, token, Now.AddHours(1), Now);
        invitation.Accept(token, Now.AddMinutes(1));
        Assert.NotNull(invitation.AcceptedAt);
        Assert.Throws<DomainRuleException>(() => invitation.Accept(token, Now.AddMinutes(2)));
    }

    public static TheoryData<MemberRole, string, bool> PermissionCases => new()
    {
        { MemberRole.Owner, Permissions.BillingManage, true },
        { MemberRole.Admin, Permissions.MembersManage, true },
        { MemberRole.Dispatcher, Permissions.JobsCreate, true },
        { MemberRole.Dispatcher, Permissions.JobsComplete, false },
        { MemberRole.Technician, Permissions.JobsComplete, true },
        { MemberRole.Technician, Permissions.JobsAssign, false },
        { MemberRole.Viewer, Permissions.AuditRead, false },
        { MemberRole.Viewer, Permissions.AssetsManage, false },
        { MemberRole.Owner, Permissions.InspectionsSubmit, true },
        { MemberRole.Admin, Permissions.AuditRead, true }
    };

    [Theory]
    [MemberData(nameof(PermissionCases))]
    public void RolePermissions_PolicyMatrix_MatchesExpected(MemberRole role, string permission, bool expected)
    {
        Assert.Equal(expected, RolePermissions.Allows(role, permission));
    }

    [Fact]
    public void TenantGuard_DifferentTenantAggregates_AreRejected()
    {
        var tenantA = Guid.NewGuid();
        var asset = new Asset(Guid.NewGuid(), "A-1", "Asset", "Tools", "Nairobi", null);
        Assert.Throws<CrossTenantAccessException>(() => TenantGuard.EnsureSameTenant(tenantA, asset));
    }

    [Fact]
    public void Money_UnsupportedCurrency_IsRejected()
    {
        Assert.Throws<DomainRuleException>(() => new Money(10, "EUR").Normalize());
    }

    private static (InspectionService Service, InspectionTemplate Template) CreateInspection()
    {
        var tenant = Guid.NewGuid();
        var template = new InspectionTemplate(tenant, "Safety check", 80);
        template.AddItem("Guard fitted", InspectionItemType.Boolean, true, 2);
        template.AddItem("Pressure", InspectionItemType.Number, true, 3, 1, 10);
        var service = new InspectionService(
            new FakeTenantContext(tenant),
            new NullInspectionRepository(),
            new FakeClock(Now));
        return (service, template);
    }

    private sealed class NullInspectionRepository : IInspectionRepository
    {
        public Task<InspectionTemplate?> FindTemplateAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<InspectionTemplate?>(null);
        public Task AddTemplateAsync(InspectionTemplate template, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AddSubmissionAsync(InspectionSubmission submission, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
