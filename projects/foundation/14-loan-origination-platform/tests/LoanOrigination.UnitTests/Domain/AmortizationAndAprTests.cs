using LoanOrigination.Domain.Calculations;
using LoanOrigination.Domain.Models;

namespace LoanOrigination.UnitTests.Domain;

public sealed class AmortizationAndAprTests
{
    [Fact]
    public void Generate_ReducingBalance_SumsPrincipalAndInterestExactly()
    {
        var schedule = AmortizationEngine.Generate(new LoanTerms(100_000m, 18m, 12, InterestRateMethod.ReducingBalance, "KES"));

        Assert.Equal(schedule.FinancedPrincipal, schedule.Installments.Sum(item => item.Principal));
        Assert.Equal(schedule.TotalInterest, schedule.Installments.Sum(item => item.Interest));
        Assert.Equal(schedule.FinancedPrincipal + schedule.TotalInterest, schedule.TotalRepayment);
        Assert.Equal(0m, schedule.Installments[^1].ClosingBalance);
    }

    [Fact]
    public void Generate_FlatInterest_SumsPrincipalAndInterestExactly()
    {
        var schedule = AmortizationEngine.Generate(new LoanTerms(100_000m, 12m, 12, InterestRateMethod.Flat, "KES"));

        Assert.Equal(100_000m, schedule.Installments.Sum(item => item.Principal));
        Assert.Equal(12_000m, schedule.TotalInterest);
        Assert.Equal(112_000m, schedule.TotalRepayment);
        Assert.Equal(0m, schedule.Installments[^1].ClosingBalance);
    }

    [Fact]
    public void Generate_RoundingResidue_AdjustsFinalInstallment()
    {
        var schedule = AmortizationEngine.Generate(new LoanTerms(1_000m, 17m, 7, InterestRateMethod.ReducingBalance, "KES"));

        Assert.Equal(1_000m, schedule.Installments.Sum(item => item.Principal));
        Assert.Equal(0m, schedule.Installments[^1].ClosingBalance);
        Assert.NotEqual(schedule.Installments[0].Payment, schedule.Installments[^1].Payment);
    }

    [Fact]
    public void Generate_ZeroTerm_ReturnsEmptySchedule()
    {
        var schedule = AmortizationEngine.Generate(new LoanTerms(100m, 12m, 0, InterestRateMethod.ReducingBalance, "KES"));

        Assert.Empty(schedule.Installments);
        Assert.Equal(0m, schedule.TotalRepayment);
    }

    [Fact]
    public void Generate_OneTerm_ProducesFullPrincipalAndInterest()
    {
        var schedule = AmortizationEngine.Generate(new LoanTerms(1_000m, 12m, 1, InterestRateMethod.ReducingBalance, "KES"));

        var installment = Assert.Single(schedule.Installments);
        Assert.Equal(1_000m, installment.Principal);
        Assert.Equal(10m, installment.Interest);
        Assert.Equal(1_010m, installment.Payment);
    }

    [Fact]
    public void Generate_ZeroRate_AmortisesOnlyPrincipal()
    {
        var schedule = AmortizationEngine.Generate(new LoanTerms(100m, 0m, 3, InterestRateMethod.ReducingBalance, "KES"));

        Assert.Equal(0m, schedule.TotalInterest);
        Assert.Equal(100m, schedule.Installments.Sum(item => item.Principal));
        Assert.Equal(100m, schedule.TotalRepayment);
    }

    [Fact]
    public void Generate_Fees_CapitalizesAndDeductsCorrectly()
    {
        var schedule = AmortizationEngine.Generate(new LoanTerms(
            100_000m,
            12m,
            12,
            InterestRateMethod.Flat,
            "KES",
            [
                new ProductFee("INS", 2m, true, FeeTreatment.Capitalized, "Insurance"),
                new ProductFee("ARR", 1m, true, FeeTreatment.Deducted, "Arrangement")
            ]));

        Assert.Equal(2_000m, schedule.CapitalizedFees);
        Assert.Equal(1_000m, schedule.DeductedFees);
        Assert.Equal(102_000m, schedule.FinancedPrincipal);
        Assert.Equal(99_000m, schedule.NetDisbursement);
    }

    [Fact]
    public void Generate_NegativePrincipal_RejectsInvalidTerms()
    {
        Assert.Throws<DomainException>(() =>
            AmortizationEngine.Generate(new LoanTerms(-1m, 12m, 12, InterestRateMethod.Flat, "KES")));
    }

    [Fact]
    public void SolveMonthlyRate_OnePeriodHandComputedValue_ReturnsTenPercent()
    {
        var rate = IrrSolver.SolveMonthlyRate([-100m, 110m]);

        Assert.InRange(rate, 0.099999m, 0.100001m);
    }

    [Fact]
    public void EffectiveAnnualRate_OnePeriodHandComputedValue_CompoundsCorrectly()
    {
        var rate = IrrSolver.EffectiveAnnualRate([-100m, 110m]);

        Assert.InRange(rate, 2.13842m, 2.13844m);
    }

    [Fact]
    public void CalculateApr_DeductedFees_IncreasesEffectiveRateAboveNominal()
    {
        var schedule = AmortizationEngine.Generate(new LoanTerms(
            100_000m,
            12m,
            12,
            InterestRateMethod.ReducingBalance,
            "KES",
            [new ProductFee("ARR", 2m, true, FeeTreatment.Deducted, "Arrangement")]));

        var apr = IrrSolver.CalculateApr(schedule);

        Assert.True(apr > 0.12m);
    }

    [Fact]
    public void SolveMonthlyRate_InvalidCashFlows_RejectsInput()
    {
        Assert.Throws<DomainException>(() => IrrSolver.SolveMonthlyRate([100m]));
    }
}
