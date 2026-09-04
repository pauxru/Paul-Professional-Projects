using LoanOrigination.Domain.Models;

namespace LoanOrigination.Domain.Calculations;

public sealed record AffordabilityPolicy(
    decimal MaximumDebtServiceRatio,
    decimal StressRateUpliftPercentagePoints,
    decimal MinimumDisposableIncomePerDependant,
    decimal ExpenseBufferRatio = 0.05m);

public sealed record AffordabilityResult(
    decimal NetIncome,
    decimal ExistingDebtService,
    decimal Expenses,
    decimal DebtServiceRatio,
    decimal DebtToIncomeRatio,
    decimal StressTestedDebtServiceRatio,
    decimal MaximumAffordableInstallment,
    decimal DerivedMaximumPrincipal,
    bool IsAffordable,
    string Reason);

public static class AffordabilityCalculator
{
    public static AffordabilityResult Calculate(
        ApplicantFacts facts,
        decimal annualRate,
        int termMonths,
        InterestRateMethod method,
        AffordabilityPolicy policy)
    {
        if (termMonths <= 0)
        {
            throw new DomainException("Term must be positive for affordability.");
        }

        var netIncome = Round(facts.MonthlyNetIncome);
        var existingDebt = Round(facts.ExistingMonthlyDebtService);
        var expenses = Round(facts.MonthlyExpenses * (1m + policy.ExpenseBufferRatio));
        var dependentReserve = Round(facts.Dependants * policy.MinimumDisposableIncomePerDependant);
        var capacityByDsr = Round(decimal.Max(0m, (netIncome * policy.MaximumDebtServiceRatio) - existingDebt));
        var capacityByCash = Round(decimal.Max(0m, netIncome - expenses - existingDebt - dependentReserve));
        var maxInstallment = decimal.Min(capacityByDsr, capacityByCash);
        var stressedMonthlyRate = (annualRate + policy.StressRateUpliftPercentagePoints) / 100m / 12m;
        var derivedPrincipal = method == InterestRateMethod.ReducingBalance
            ? PresentValueOfAnnuity(maxInstallment, stressedMonthlyRate, termMonths)
            : FlatPrincipal(maxInstallment, annualRate + policy.StressRateUpliftPercentagePoints, termMonths);
        derivedPrincipal = Round(decimal.Max(0m, derivedPrincipal));
        var dsr = netIncome <= 0m ? decimal.MaxValue : existingDebt / netIncome;
        var dti = netIncome <= 0m ? decimal.MaxValue : (existingDebt + facts.MonthlyExpenses) / netIncome;
        var stressedPaymentForRequest = PaymentForPrincipal(
            facts.RequestedPrincipal,
            stressedMonthlyRate,
            termMonths,
            method,
            annualRate + policy.StressRateUpliftPercentagePoints);
        var stressedDsr = netIncome <= 0m ? decimal.MaxValue : (existingDebt + stressedPaymentForRequest) / netIncome;
        var affordable = facts.RequestedPrincipal <= derivedPrincipal &&
                         stressedDsr <= policy.MaximumDebtServiceRatio &&
                         maxInstallment > 0m;

        return new AffordabilityResult(
            netIncome,
            existingDebt,
            expenses,
            dsr,
            dti,
            stressedDsr,
            maxInstallment,
            derivedPrincipal,
            affordable,
            affordable ? "Requested terms pass configured DSR, cash-flow, and stress-test limits." :
                "Requested terms exceed configured DSR, cash-flow, or stress-test limits.");
    }

    private static decimal PresentValueOfAnnuity(decimal payment, decimal monthlyRate, int months)
    {
        if (monthlyRate == 0m)
        {
            return payment * months;
        }

        return payment * (1m - DecimalPower(1m + monthlyRate, -months)) / monthlyRate;
    }

    private static decimal FlatPrincipal(decimal payment, decimal annualRate, int months)
    {
        var factor = 1m + (annualRate / 100m * months / 12m);
        return factor == 0m ? 0m : payment * months / factor;
    }

    private static decimal PaymentForPrincipal(
        decimal principal,
        decimal stressedMonthlyRate,
        int months,
        InterestRateMethod method,
        decimal stressedAnnualRate)
    {
        if (method == InterestRateMethod.Flat)
        {
            return Round((principal + (principal * stressedAnnualRate / 100m * months / 12m)) / months);
        }

        if (stressedMonthlyRate == 0m)
        {
            return Round(principal / months);
        }

        return Round(principal * stressedMonthlyRate /
            (1m - DecimalPower(1m + stressedMonthlyRate, -months)));
    }

    private static decimal DecimalPower(decimal baseValue, int exponent)
    {
        var result = 1m;
        for (var index = 0; index < Math.Abs(exponent); index++)
        {
            result *= baseValue;
        }

        return exponent < 0 ? 1m / result : result;
    }

    private static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

public static class IrrSolver
{
    public static decimal SolveMonthlyRate(IReadOnlyList<decimal> cashFlows, decimal initialGuess = 0.02m)
    {
        if (cashFlows.Count < 2 || cashFlows[0] >= 0m)
        {
            throw new DomainException("IRR requires an initial cash outflow followed by at least one cash flow.");
        }

        var rate = initialGuess;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var (value, derivative) = Evaluate(cashFlows, rate);
            if (decimal.Abs(value) < 0.0000001m)
            {
                return rate;
            }

            if (decimal.Abs(derivative) < 0.0000000001m)
            {
                break;
            }

            var next = rate - (value / derivative);
            if (next <= -0.999m || next > 10m)
            {
                break;
            }

            if (decimal.Abs(next - rate) < 0.0000001m)
            {
                return next;
            }

            rate = next;
        }

        return SolveByBisection(cashFlows);
    }

    public static decimal EffectiveAnnualRate(IReadOnlyList<decimal> cashFlows)
    {
        var monthly = SolveMonthlyRate(cashFlows);
        var annual = DecimalPower(1m + monthly, 12) - 1m;
        return decimal.Round(annual, 8, MidpointRounding.AwayFromZero);
    }

    public static decimal CalculateApr(AmortizationSchedule schedule)
    {
        if (schedule.Installments.Count == 0 || schedule.NetDisbursement <= 0m)
        {
            return 0m;
        }

        var cashFlows = new List<decimal> { -schedule.NetDisbursement };
        cashFlows.AddRange(schedule.Installments.Select(installment => installment.Payment));
        return EffectiveAnnualRate(cashFlows);
    }

    private static (decimal Value, decimal Derivative) Evaluate(IReadOnlyList<decimal> cashFlows, decimal rate)
    {
        var value = 0m;
        var derivative = 0m;
        var onePlusRate = 1m + rate;
        for (var period = 0; period < cashFlows.Count; period++)
        {
            var discount = DecimalPower(onePlusRate, period);
            value += cashFlows[period] / discount;
            if (period > 0)
            {
                derivative -= period * cashFlows[period] / (discount * onePlusRate);
            }
        }

        return (value, derivative);
    }

    private static decimal SolveByBisection(IReadOnlyList<decimal> cashFlows)
    {
        var lower = -0.999m;
        var upper = 10m;
        var lowerValue = Evaluate(cashFlows, lower).Value;
        var upperValue = Evaluate(cashFlows, upper).Value;
        if (Math.Sign(lowerValue) == Math.Sign(upperValue))
        {
            throw new DomainException("IRR could not be bracketed.");
        }

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var mid = (lower + upper) / 2m;
            var midValue = Evaluate(cashFlows, mid).Value;
            if (decimal.Abs(midValue) < 0.0000001m)
            {
                return mid;
            }

            if (Math.Sign(midValue) == Math.Sign(lowerValue))
            {
                lower = mid;
                lowerValue = midValue;
            }
            else
            {
                upper = mid;
                upperValue = midValue;
            }
        }

        return (lower + upper) / 2m;
    }

    private static decimal DecimalPower(decimal baseValue, int exponent)
    {
        var result = 1m;
        for (var index = 0; index < exponent; index++)
        {
            result *= baseValue;
        }

        return result;
    }
}
