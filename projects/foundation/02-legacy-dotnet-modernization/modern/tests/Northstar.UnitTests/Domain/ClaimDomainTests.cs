using Northstar.Domain.Claims;
using Northstar.Domain.Common;
using Northstar.Domain.Policies;

namespace Northstar.UnitTests.Domain;

public sealed class ClaimDomainTests
{
    private readonly SettlementCalculator _calculator = new();

    [Fact]
    public void Calculate_NormalLoss_AppliesDeductible()
    {
        var result = _calculator.CalculateAmount(3_400m, 500m, 10_000m);
        Assert.Equal(2_900m, result);
    }

    [Fact]
    public void Calculate_LossAboveLimit_CapsAtPolicyLimit()
    {
        var result = _calculator.CalculateAmount(20_000m, 250m, 5_000m);
        Assert.Equal(5_000m, result);
    }

    [Fact]
    public void Calculate_DeductibleAboveClaim_ReturnsZero()
    {
        var result = _calculator.CalculateAmount(100m, 500m, 10_000m);
        Assert.Equal(0m, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_ZeroOrNegativeClaim_ReturnsZero(decimal amount)
    {
        Assert.Equal(0m, _calculator.CalculateAmount(amount, 10m, 1_000m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_ZeroOrNegativeLimit_ReturnsZero(decimal limit)
    {
        Assert.Equal(0m, _calculator.CalculateAmount(100m, 10m, limit));
    }

    [Fact]
    public void Calculate_NegativeDeductible_PreservesCharacterizedLegacyQuirk()
    {
        Assert.Equal(100m, _calculator.CalculateAmount(100m, -50m, 1_000m));
    }

    [Fact]
    public void Calculate_FractionalPayout_RoundsAwayFromZero()
    {
        Assert.Equal(1_000.01m, _calculator.CalculateAmount(1_000.005m, 0m, 10_000m));
    }

    [Fact]
    public void Calculate_MismatchedCurrencies_Throws()
    {
        var exception = Assert.Throws<DomainRuleException>(() =>
            _calculator.Calculate(new Money(100m, "USD"), new Money(10m, "KES"), new Money(1_000m, "USD")));
        Assert.Contains("same currency", exception.Message);
    }

    [Theory]
    [InlineData(ClaimStatus.Submitted, ClaimStatus.UnderReview)]
    [InlineData(ClaimStatus.UnderReview, ClaimStatus.Approved)]
    [InlineData(ClaimStatus.UnderReview, ClaimStatus.Rejected)]
    [InlineData(ClaimStatus.Approved, ClaimStatus.Settled)]
    [InlineData(ClaimStatus.Approved, ClaimStatus.Closed)]
    [InlineData(ClaimStatus.Rejected, ClaimStatus.Closed)]
    [InlineData(ClaimStatus.Settled, ClaimStatus.Closed)]
    public void IsAllowedTransition_ValidEdge_ReturnsTrue(ClaimStatus from, ClaimStatus to)
    {
        Assert.True(Claim.IsAllowedTransition(from, to));
    }

    [Theory]
    [InlineData(ClaimStatus.Submitted, ClaimStatus.Approved)]
    [InlineData(ClaimStatus.Rejected, ClaimStatus.Settled)]
    [InlineData(ClaimStatus.Closed, ClaimStatus.UnderReview)]
    public void IsAllowedTransition_InvalidEdge_ReturnsFalse(ClaimStatus from, ClaimStatus to)
    {
        Assert.False(Claim.IsAllowedTransition(from, to));
    }

    [Fact]
    public void Transition_InvalidDirectApproval_Throws()
    {
        var claim = NewClaim();
        Assert.Throws<DomainRuleException>(() => claim.TransitionTo(ClaimStatus.Approved));
    }

    [Fact]
    public void AssignAdjuster_BeforeReview_Throws()
    {
        var claim = NewClaim();
        Assert.Throws<DomainRuleException>(() => claim.AssignAdjuster("A. Adjuster"));
    }

    [Fact]
    public void Assessment_ReviewAssignmentAndReserve_IncrementsVersion()
    {
        var claim = NewClaim();
        claim.StartReview();
        claim.AssignAdjuster("A. Adjuster");
        claim.SetReserve(900m);

        Assert.Equal(4, claim.Version);
        Assert.Equal("A. Adjuster", claim.AssignedAdjuster);
        Assert.Equal(900m, claim.ReserveAmount);
    }

    [Fact]
    public void SetReserve_NegativeAmount_Throws()
    {
        Assert.Throws<DomainRuleException>(() => NewClaim().SetReserve(-1m));
    }

    [Fact]
    public void RecordSettlement_BeforeSettled_Throws()
    {
        Assert.Throws<DomainRuleException>(() => NewClaim().RecordSettlement(10m));
    }

    [Fact]
    public void RecordSettlement_SettledClaim_StoresAmount()
    {
        var claim = NewClaim();
        claim.StartReview();
        claim.TransitionTo(ClaimStatus.Approved);
        claim.TransitionTo(ClaimStatus.Settled);
        claim.RecordSettlement(900m);

        Assert.Equal(900m, claim.SettlementAmount);
        Assert.Equal(5, claim.Version);
    }

    [Fact]
    public void Create_NonPositiveClaim_Throws()
    {
        Assert.Throws<DomainRuleException>(() =>
            Claim.Create(Guid.NewGuid(), Guid.NewGuid(), "CLM-INVALID", 0m, "USD", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Policy_NegativeDeductible_Throws()
    {
        Assert.Throws<DomainRuleException>(() =>
            new Policy(Guid.NewGuid(), Guid.NewGuid(), "POL-1", -1m, 1m, "USD"));
    }

    [Fact]
    public void Money_InvalidCurrency_Throws()
    {
        Assert.Throws<DomainRuleException>(() => new Money(10m, "US"));
    }

    private static Claim NewClaim() =>
        Claim.Create(Guid.NewGuid(), Guid.NewGuid(), "CLM-UNIT-1", 1_000m, "USD", DateTimeOffset.UnixEpoch);
}
