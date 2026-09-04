using LoanOrigination.Domain.Calculations;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Risk;

namespace LoanOrigination.UnitTests.Domain;

public sealed class AffordabilityAndScorecardTests
{
    private static readonly AffordabilityPolicy Policy = new(0.55m, 5m, 2_500m);

    [Fact]
    public void Calculate_SustainableIncome_ReturnsAffordableAmount()
    {
        var result = AffordabilityCalculator.Calculate(TestData.Facts(requested: 100_000m), 18m, 12, InterestRateMethod.ReducingBalance, Policy);

        Assert.True(result.IsAffordable);
        Assert.True(result.MaximumAffordableInstallment > 0m);
        Assert.True(result.DerivedMaximumPrincipal >= 100_000m);
    }

    [Fact]
    public void Calculate_StressRateUplift_ReducesDerivedPrincipal()
    {
        var withoutStress = AffordabilityCalculator.Calculate(
            TestData.Facts(requested: 100_000m),
            18m,
            24,
            InterestRateMethod.ReducingBalance,
            Policy with { StressRateUpliftPercentagePoints = 0m });
        var withStress = AffordabilityCalculator.Calculate(TestData.Facts(requested: 100_000m), 18m, 24, InterestRateMethod.ReducingBalance, Policy);

        Assert.True(withStress.DerivedMaximumPrincipal < withoutStress.DerivedMaximumPrincipal);
    }

    [Fact]
    public void Calculate_OverextendedDebtService_FailsStressBoundary()
    {
        var facts = TestData.Facts(income: 100_000m, debt: 50_000m, requested: 300_000m);
        var result = AffordabilityCalculator.Calculate(facts, 18m, 12, InterestRateMethod.ReducingBalance, Policy);

        Assert.False(result.IsAffordable);
        Assert.True(result.StressTestedDebtServiceRatio > Policy.MaximumDebtServiceRatio);
    }

    [Fact]
    public void Calculate_ZeroTerm_RejectsCalculation()
    {
        Assert.Throws<DomainException>(() =>
            AffordabilityCalculator.Calculate(TestData.Facts(), 12m, 0, InterestRateMethod.Flat, Policy));
    }

    [Theory]
    [InlineData(85, "A")]
    [InlineData(70, "B")]
    [InlineData(55, "C")]
    [InlineData(40, "D")]
    [InlineData(39, "E")]
    public void BandFor_Boundaries_ReturnExpectedBand(int score, string expected)
    {
        Assert.Equal(expected, WeightedScorecard.BandFor(score));
    }

    [Fact]
    public void Evaluate_TransparentScorecard_ReturnsSixFactorBreakdown()
    {
        var assessment = new WeightedScorecard().Evaluate(
            TestData.Facts(),
            new BureauReport("B", 0, 0m, "Synthetic report"));

        Assert.Equal(6, assessment.Contributions.Count);
        Assert.Equal(assessment.Score, decimal.ToInt32(decimal.Round(assessment.Contributions.Sum(item => item.Points), 0, MidpointRounding.AwayFromZero)));
        Assert.All(assessment.Contributions, contribution => Assert.False(string.IsNullOrWhiteSpace(contribution.Explanation)));
    }

    [Fact]
    public void Evaluate_MoreArrears_ReducesArrearsContribution()
    {
        var scorecard = new WeightedScorecard();
        var clean = scorecard.Evaluate(TestData.Facts(arrears: 0), new BureauReport("B", 0, 0m, "Clean"));
        var impaired = scorecard.Evaluate(TestData.Facts(arrears: 3), new BureauReport("B", 3, 0m, "Impaired"));

        var cleanArrears = clean.Contributions.Single(item => item.Factor == "Past arrears").Points;
        var impairedArrears = impaired.Contributions.Single(item => item.Factor == "Past arrears").Points;
        Assert.True(impairedArrears < cleanArrears);
        Assert.True(impaired.Score < clean.Score);
    }
}
