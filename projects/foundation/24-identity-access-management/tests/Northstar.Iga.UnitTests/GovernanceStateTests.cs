using Northstar.Iga.Domain;

namespace Northstar.Iga.UnitTests;

public sealed class SoDEngineTests
{
    [Fact]
    public void Detect_ToxicCombination_ReturnsViolation()
    {
        var fixture = Fixture();
        var violations = SoDEngine.Detect(
            fixture.UserId,
            [fixture.Rule.EntitlementAId, fixture.Rule.EntitlementBId],
            [fixture.Rule],
            [],
            fixture.Now);
        Assert.Single(violations);
        Assert.False(violations[0].HasActiveException);
    }

    [Fact]
    public void Detect_ActiveApprovedException_AnnotatesViolation()
    {
        var fixture = Fixture();
        var exception = new SoDException
        {
            RuleId = fixture.Rule.Id,
            UserId = fixture.UserId,
            ExpiresAt = fixture.Now.AddDays(1)
        };
        var violation = Assert.Single(SoDEngine.Detect(
            fixture.UserId,
            [fixture.Rule.EntitlementAId, fixture.Rule.EntitlementBId],
            [fixture.Rule],
            [exception],
            fixture.Now));
        Assert.True(violation.HasActiveException);
    }

    [Fact]
    public void Detect_ExpiredException_DoesNotSuppressViolation()
    {
        var fixture = Fixture();
        var exception = new SoDException
        {
            RuleId = fixture.Rule.Id,
            UserId = fixture.UserId,
            ExpiresAt = fixture.Now.AddSeconds(-1)
        };
        var violation = Assert.Single(SoDEngine.Detect(
            fixture.UserId,
            [fixture.Rule.EntitlementAId, fixture.Rule.EntitlementBId],
            [fixture.Rule],
            [exception],
            fixture.Now));
        Assert.False(violation.HasActiveException);
    }

    [Fact]
    public void Detect_OnlyOneEntitlement_ReturnsEmpty()
    {
        var fixture = Fixture();
        Assert.Empty(SoDEngine.Detect(
            fixture.UserId,
            [fixture.Rule.EntitlementAId],
            [fixture.Rule],
            [],
            fixture.Now));
    }

    private static (Guid UserId, SoDRule Rule, DateTimeOffset Now) Fixture() =>
        (Guid.NewGuid(),
            new SoDRule
            {
                Name = "Toxic",
                EntitlementAId = Guid.NewGuid(),
                EntitlementBId = Guid.NewGuid()
            },
            new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
}

public sealed class ApprovalWorkflowStateMachineTests
{
    [Fact]
    public void ActivateFirstStage_ParallelSteps_OnlyFirstStageBecomesPending()
    {
        var request = Request();
        var steps = Steps();
        ApprovalWorkflowStateMachine.ActivateFirstStage(request, steps);
        Assert.All(steps.Where(x => x.Stage == 1), x => Assert.Equal(ApprovalStepStatus.Pending, x.Status));
        Assert.All(steps.Where(x => x.Stage == 2), x => Assert.Equal(ApprovalStepStatus.Waiting, x.Status));
    }

    [Fact]
    public void Approve_OneParallelStep_DoesNotAdvanceStage()
    {
        var request = Request();
        var steps = Steps(twoAtFirstStage: true);
        ApprovalWorkflowStateMachine.ActivateFirstStage(request, steps);
        var first = steps[0];
        Assert.False(ApprovalWorkflowStateMachine.Approve(
            request, steps, first.Id, first.ApproverId, "Approved", Now));
        Assert.Equal(1, request.CurrentStage);
    }

    [Fact]
    public void Approve_LastStage_CompletesRequest()
    {
        var request = Request();
        var step = new ApprovalStep { RequestId = request.Id, Stage = 1, ApproverId = Guid.NewGuid() };
        step.OriginalApproverId = step.ApproverId;
        ApprovalWorkflowStateMachine.ActivateFirstStage(request, [step]);
        Assert.True(ApprovalWorkflowStateMachine.Approve(
            request, [step], step.Id, step.ApproverId, "Approved", Now));
        Assert.Equal(AccessRequestStatus.Approved, request.Status);
    }

    [Fact]
    public void Reject_WithoutReason_Throws()
    {
        var request = Request();
        var step = Steps()[0];
        ApprovalWorkflowStateMachine.ActivateFirstStage(request, [step]);
        Assert.Throws<DomainRuleException>(() =>
            ApprovalWorkflowStateMachine.Reject(request, [step], step.Id, step.ApproverId, "", Now));
    }

    [Fact]
    public void Reject_WithReason_RejectsRequest()
    {
        var request = Request();
        var step = Steps()[0];
        ApprovalWorkflowStateMachine.ActivateFirstStage(request, [step]);
        ApprovalWorkflowStateMachine.Reject(request, [step], step.Id, step.ApproverId, "Insufficient need", Now);
        Assert.Equal(AccessRequestStatus.Rejected, request.Status);
        Assert.Equal("Insufficient need", request.RejectionReason);
    }

    [Fact]
    public void Delegate_ActiveStep_ChangesApprover()
    {
        var step = Steps()[0];
        step.Status = ApprovalStepStatus.Pending;
        var original = step.ApproverId;
        var replacement = Guid.NewGuid();
        ApprovalWorkflowStateMachine.Delegate(step, original, replacement);
        Assert.Equal(replacement, step.ApproverId);
        Assert.True(step.WasDelegated);
    }

    [Fact]
    public void Delegate_ToSelf_Throws()
    {
        var step = Steps()[0];
        step.Status = ApprovalStepStatus.Pending;
        Assert.Throws<DomainRuleException>(() =>
            ApprovalWorkflowStateMachine.Delegate(step, step.ApproverId, step.ApproverId));
    }

    [Fact]
    public void EscalateOverdue_ChangesApproverAndExtendsSla()
    {
        var request = Request();
        var step = Steps()[0];
        step.Status = ApprovalStepStatus.Pending;
        step.DueAt = Now.AddMinutes(-1);
        var escalationApprover = Guid.NewGuid();
        var count = ApprovalWorkflowStateMachine.EscalateOverdue(
            request, [step], Now, escalationApprover, TimeSpan.FromHours(8));
        Assert.Equal(1, count);
        Assert.Equal(escalationApprover, step.ApproverId);
        Assert.True(step.WasEscalated);
        Assert.Equal(Now.AddHours(8), step.DueAt);
    }

    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    private static AccessRequest Request() => new() { Status = AccessRequestStatus.Pending };

    private static List<ApprovalStep> Steps(bool twoAtFirstStage = false)
    {
        var first = new ApprovalStep { Stage = 1, ApproverId = Guid.NewGuid(), DueAt = Now.AddHours(1) };
        first.OriginalApproverId = first.ApproverId;
        var second = new ApprovalStep
        {
            Stage = twoAtFirstStage ? 1 : 2,
            ApproverId = Guid.NewGuid(),
            DueAt = Now.AddHours(1)
        };
        second.OriginalApproverId = second.ApproverId;
        return [first, second];
    }
}

public sealed class IdentityStateTests
{
    [Fact]
    public void Activate_TerminatedIdentity_Throws()
    {
        var user = new UserIdentity { Status = IdentityStatus.Terminated };
        Assert.Throws<DomainRuleException>(user.Activate);
    }

    [Fact]
    public void Terminate_RecordsSessionTerminationAndEndDate()
    {
        var at = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var user = new UserIdentity { Status = IdentityStatus.Active };
        user.Terminate(at);
        Assert.Equal(IdentityStatus.Terminated, user.Status);
        Assert.Equal(at, user.SessionsTerminatedAt);
        Assert.Equal(new DateOnly(2026, 9, 3), user.EndDate);
    }
}
