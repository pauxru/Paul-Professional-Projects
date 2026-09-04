using Northstar.Domain.Common;

namespace Northstar.Domain.Policies;

public sealed class Policy
{
    private Policy()
    {
    }

    public Policy(
        Guid id,
        Guid policyholderId,
        string policyNumber,
        decimal deductibleAmount,
        decimal limitAmount,
        string currency)
    {
        if (id == Guid.Empty || policyholderId == Guid.Empty || string.IsNullOrWhiteSpace(policyNumber))
        {
            throw new DomainRuleException("Policy identity, holder and number are required.");
        }

        if (deductibleAmount < 0 || limitAmount <= 0)
        {
            throw new DomainRuleException("Policy deductible must be non-negative and limit must be positive.");
        }

        Id = id;
        PolicyholderId = policyholderId;
        PolicyNumber = policyNumber.Trim().ToUpperInvariant();
        DeductibleAmount = deductibleAmount;
        LimitAmount = limitAmount;
        Currency = new Money(0m, currency).Currency;
    }

    public Guid Id { get; private set; }
    public Guid PolicyholderId { get; private set; }
    public string PolicyNumber { get; private set; } = string.Empty;
    public decimal DeductibleAmount { get; private set; }
    public decimal LimitAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public Policyholder? Policyholder { get; private set; }
}
