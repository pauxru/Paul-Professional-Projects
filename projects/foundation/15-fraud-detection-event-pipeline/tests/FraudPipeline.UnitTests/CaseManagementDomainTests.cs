using FraudPipeline.Domain.Entities;

namespace FraudPipeline.UnitTests;

public class CaseManagementDomainTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewCase_StartsInNewStatus()
    {
        var c = new Case(Guid.NewGuid(), "Card:CARD1", "USD", _now);
        Assert.Equal(CaseStatus.New, c.Status);
        Assert.Equal(CaseDisposition.Unresolved, c.Disposition);
    }

    [Fact]
    public void LinkAlert_IncrementsExposureAndPriority()
    {
        var c = new Case(Guid.NewGuid(), "Card:CARD1", "USD", _now);
        c.LinkAlert(Guid.NewGuid(), 700, 500m, _now);
        c.LinkAlert(Guid.NewGuid(), 900, 1500m, _now.AddMinutes(1));
        Assert.Equal(2000m, c.ExposureAmount);
        Assert.Equal(900, c.PriorityScore);
    }

    [Fact]
    public void Assign_TransitionsToAssigned()
    {
        var c = new Case(Guid.NewGuid(), "Card:CARD1", "USD", _now);
        c.Assign("analyst1", _now);
        Assert.Equal(CaseStatus.Assigned, c.Status);
        Assert.Equal("analyst1", c.AssignedTo);
    }

    [Fact]
    public void StateMachine_ProposeDispositionWithoutAssignment_Throws()
    {
        var c = new Case(Guid.NewGuid(), "Card:CARD1", "USD", _now);
        Assert.Throws<InvalidOperationException>(() => c.ProposeDisposition(CaseDisposition.FalsePositive, "reason", "analyst", _now));
    }

    [Fact]
    public void SmallExposure_ConfirmedFraud_DisposesImmediately()
    {
        var c = new Case(Guid.NewGuid(), "Card:CARD1", "USD", _now);
        c.LinkAlert(Guid.NewGuid(), 700, 500m, _now);
        c.Assign("analyst1", _now);
        c.ProposeDisposition(CaseDisposition.ConfirmedFraud, "clear fraud", "analyst1", _now);
        Assert.Equal(CaseStatus.Disposed, c.Status);
        Assert.NotNull(c.DisposedAt);
    }

    [Fact]
    public void LargeExposure_ConfirmedFraud_RequiresFourEyes()
    {
        var c = new Case(Guid.NewGuid(), "Card:CARD1", "USD", _now);
        c.LinkAlert(Guid.NewGuid(), 900, 25000m, _now);
        c.Assign("analyst1", _now);
        c.ProposeDisposition(CaseDisposition.ConfirmedFraud, "large loss", "analyst1", _now);
        Assert.Equal(CaseStatus.AwaitingApproval, c.Status);
        Assert.Null(c.DisposedAt);
        Assert.Throws<InvalidOperationException>(() => c.Approve("analyst1", _now));
        c.Approve("supervisor1", _now);
        Assert.Equal(CaseStatus.Disposed, c.Status);
        Assert.Equal("supervisor1", c.ApprovedBy);
    }

    [Fact]
    public void FalsePositive_DoesNotRequireFourEyes()
    {
        var c = new Case(Guid.NewGuid(), "Card:CARD1", "USD", _now);
        c.LinkAlert(Guid.NewGuid(), 900, 25000m, _now);
        c.Assign("analyst1", _now);
        c.ProposeDisposition(CaseDisposition.FalsePositive, "chargeback reversed", "analyst1", _now);
        Assert.Equal(CaseStatus.Disposed, c.Status);
    }

    [Fact]
    public void AddNote_AfterDisposition_Throws()
    {
        var c = new Case(Guid.NewGuid(), "Card:CARD1", "USD", _now);
        c.Assign("analyst1", _now);
        c.ProposeDisposition(CaseDisposition.Inconclusive, "unclear", "analyst1", _now);
        Assert.Throws<InvalidOperationException>(() => c.AddNote(Guid.NewGuid(), "analyst1", "another", _now.AddMinutes(1)));
    }
}
