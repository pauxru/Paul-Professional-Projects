using Northstar.Domain.Common;

namespace Northstar.Domain.Claims;

public sealed class SettlementCalculator
{
    public Money Calculate(Money claimedAmount, Money deductible, Money policyLimit)
    {
        if (!string.Equals(claimedAmount.Currency, deductible.Currency, StringComparison.Ordinal) ||
            !string.Equals(claimedAmount.Currency, policyLimit.Currency, StringComparison.Ordinal))
        {
            throw new DomainRuleException("Settlement inputs must use the same currency.");
        }

        return new Money(
            CalculateAmount(claimedAmount.Amount, deductible.Amount, policyLimit.Amount),
            claimedAmount.Currency);
    }

    public decimal CalculateAmount(decimal claimedAmount, decimal deductible, decimal policyLimit)
    {
        // Matches the characterized legacy arithmetic while making the guard explicit and testable.
        if (claimedAmount <= 0 || policyLimit <= 0)
        {
            return 0m;
        }

        var afterExcess = claimedAmount - Math.Max(deductible, 0m);
        return decimal.Round(Math.Min(Math.Max(afterExcess, 0m), policyLimit), 2, MidpointRounding.AwayFromZero);
    }
}
