using LoanOrigination.Domain.Models;

namespace LoanOrigination.Domain.Calculations;

public sealed record LoanTerms(
    decimal Principal,
    decimal AnnualInterestRate,
    int TermMonths,
    InterestRateMethod InterestMethod,
    string Currency,
    IReadOnlyList<ProductFee>? Fees = null);

public sealed record AmortizationInstallment(
    int Number,
    decimal OpeningBalance,
    decimal Principal,
    decimal Interest,
    decimal Payment,
    decimal ClosingBalance);

public sealed record AmortizationSchedule(
    decimal FinancedPrincipal,
    decimal TotalInterest,
    decimal CapitalizedFees,
    decimal DeductedFees,
    IReadOnlyList<AmortizationInstallment> Installments)
{
    public decimal TotalRepayment => Installments.Sum(item => item.Payment);
    public decimal NetDisbursement => FinancedPrincipal - CapitalizedFees - DeductedFees;
}

public static class AmortizationEngine
{
    public static AmortizationSchedule Generate(LoanTerms terms)
    {
        if (terms.Principal < 0m)
        {
            throw new DomainException("Principal cannot be negative.");
        }

        if (terms.TermMonths < 0)
        {
            throw new DomainException("Term cannot be negative.");
        }

        if (terms.AnnualInterestRate < 0m)
        {
            throw new DomainException("Interest rate cannot be negative.");
        }

        var fees = terms.Fees ?? [];
        var capitalizedFees = Round(fees.Where(fee => fee.Treatment == FeeTreatment.Capitalized)
            .Sum(fee => CalculateFee(fee, terms.Principal)));
        var deductedFees = Round(fees.Where(fee => fee.Treatment == FeeTreatment.Deducted)
            .Sum(fee => CalculateFee(fee, terms.Principal)));
        var financedPrincipal = Round(terms.Principal + capitalizedFees);

        if (terms.TermMonths == 0)
        {
            return new AmortizationSchedule(financedPrincipal, 0m, capitalizedFees, deductedFees, []);
        }

        return terms.InterestMethod switch
        {
            InterestRateMethod.ReducingBalance => BuildReducing(terms, financedPrincipal, capitalizedFees, deductedFees),
            InterestRateMethod.Flat => BuildFlat(terms, financedPrincipal, capitalizedFees, deductedFees),
            _ => throw new DomainException("Unsupported interest method.")
        };
    }

    private static AmortizationSchedule BuildReducing(
        LoanTerms terms,
        decimal financedPrincipal,
        decimal capitalizedFees,
        decimal deductedFees)
    {
        var monthlyRate = terms.AnnualInterestRate / 100m / 12m;
        var payment = monthlyRate == 0m
            ? financedPrincipal / terms.TermMonths
            : financedPrincipal * monthlyRate / (1m - DecimalPower(1m + monthlyRate, -terms.TermMonths));
        var regularPayment = Round(payment);
        var balance = financedPrincipal;
        var rows = new List<AmortizationInstallment>(terms.TermMonths);

        for (var number = 1; number <= terms.TermMonths; number++)
        {
            var interest = Round(balance * monthlyRate);
            decimal principal;
            decimal actualPayment;

            if (number == terms.TermMonths)
            {
                principal = balance;
                actualPayment = Round(principal + interest);
            }
            else
            {
                principal = Round(regularPayment - interest);
                if (principal <= 0m)
                {
                    throw new DomainException("Interest rate produces a non-amortising loan.");
                }

                if (principal > balance)
                {
                    principal = balance;
                }

                actualPayment = Round(principal + interest);
            }

            var closing = Round(balance - principal);
            rows.Add(new AmortizationInstallment(number, balance, principal, interest, actualPayment, closing));
            balance = closing;
        }

        var totalInterest = Round(rows.Sum(row => row.Interest));
        return new AmortizationSchedule(financedPrincipal, totalInterest, capitalizedFees, deductedFees, rows);
    }

    private static AmortizationSchedule BuildFlat(
        LoanTerms terms,
        decimal financedPrincipal,
        decimal capitalizedFees,
        decimal deductedFees)
    {
        var totalInterest = Round(financedPrincipal * terms.AnnualInterestRate / 100m * terms.TermMonths / 12m);
        var regularPrincipal = Round(financedPrincipal / terms.TermMonths);
        var regularInterest = Round(totalInterest / terms.TermMonths);
        var remainingPrincipal = financedPrincipal;
        var remainingInterest = totalInterest;
        var rows = new List<AmortizationInstallment>(terms.TermMonths);

        for (var number = 1; number <= terms.TermMonths; number++)
        {
            var principal = number == terms.TermMonths ? remainingPrincipal : decimal.Min(regularPrincipal, remainingPrincipal);
            var interest = number == terms.TermMonths ? remainingInterest : decimal.Min(regularInterest, remainingInterest);
            principal = Round(principal);
            interest = Round(interest);
            var closing = Round(remainingPrincipal - principal);
            rows.Add(new AmortizationInstallment(
                number,
                remainingPrincipal,
                principal,
                interest,
                Round(principal + interest),
                closing));
            remainingPrincipal = closing;
            remainingInterest = Round(remainingInterest - interest);
        }

        return new AmortizationSchedule(financedPrincipal, totalInterest, capitalizedFees, deductedFees, rows);
    }

    private static decimal CalculateFee(ProductFee fee, decimal principal) =>
        fee.IsPercentage ? principal * fee.Amount / 100m : fee.Amount;

    private static decimal DecimalPower(decimal baseValue, int exponent)
    {
        if (exponent == 0)
        {
            return 1m;
        }

        var result = 1m;
        var count = Math.Abs(exponent);
        for (var index = 0; index < count; index++)
        {
            result *= baseValue;
        }

        return exponent < 0 ? 1m / result : result;
    }

    private static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
