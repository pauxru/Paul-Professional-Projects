using System.Security.Cryptography;
using System.Text;
using LoanOrigination.Domain.Calculations;
using LoanOrigination.Domain.Models;

namespace LoanOrigination.Domain.Workflow;

public sealed record DelegatedAuthorityPolicy(
    decimal FourEyesExposureThreshold,
    IReadOnlyDictionary<UnderwriterRole, decimal> Limits)
{
    public static DelegatedAuthorityPolicy Default { get; } = new(
        500_000m,
        new Dictionary<UnderwriterRole, decimal>
        {
            [UnderwriterRole.Junior] = 250_000m,
            [UnderwriterRole.Senior] = 1_000_000m,
            [UnderwriterRole.CreditCommittee] = decimal.MaxValue
        });
}

public static class UnderwritingAuthority
{
    public static void ValidateApproval(
        decimal exposure,
        string decisionMaker,
        UnderwriterRole decisionMakerRole,
        string? secondApprover,
        UnderwriterRole? secondApproverRole,
        DelegatedAuthorityPolicy? policy = null)
    {
        var configured = policy ?? DelegatedAuthorityPolicy.Default;
        if (string.IsNullOrWhiteSpace(decisionMaker))
        {
            throw new DomainException("An underwriter identity is required.");
        }

        if (!configured.Limits.TryGetValue(decisionMakerRole, out var decisionMakerLimit) ||
            exposure > decisionMakerLimit)
        {
            throw new DomainException("Decision maker lacks delegated authority for this exposure.");
        }

        if (exposure <= configured.FourEyesExposureThreshold)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(secondApprover) ||
            string.Equals(decisionMaker, secondApprover, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Four-eyes approval requires a distinct second approver.");
        }

        if (!secondApproverRole.HasValue ||
            !configured.Limits.TryGetValue(secondApproverRole.Value, out var approverLimit) ||
            exposure > approverLimit)
        {
            throw new DomainException("Second approver lacks delegated authority for this exposure.");
        }
    }

    public static int CalculatePriority(
        decimal principal,
        string riskBand,
        DateTimeOffset queuedAt,
        DateTimeOffset slaDueAt,
        DateTimeOffset now)
    {
        var riskWeight = riskBand.ToUpperInvariant() switch
        {
            "E" => 50,
            "D" => 40,
            "C" => 30,
            "B" => 20,
            _ => 10
        };
        var valueWeight = decimal.ToInt32(decimal.Min(40m, principal / 25_000m));
        var ageHours = decimal.Max(0m, (decimal)(now - queuedAt).TotalHours);
        var ageWeight = decimal.ToInt32(decimal.Min(30m, ageHours));
        var breachWeight = now > slaDueAt ? 100 : 0;
        return riskWeight + valueWeight + ageWeight + breachWeight;
    }

    public static UnderwritingQueueItem Claim(
        UnderwritingQueueItem item,
        string actor,
        DateTimeOffset now,
        TimeSpan lockDuration)
    {
        if (item.LockExpiresAt.HasValue && item.LockExpiresAt.Value > now &&
            !string.Equals(item.ClaimedBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("The underwriting work item is currently locked.");
        }

        return item with { ClaimedBy = actor, LockExpiresAt = now.Add(lockDuration) };
    }
}

public static class OfferLifecycle
{
    public static LoanOffer Create(
        Guid applicationId,
        int version,
        decimal principal,
        decimal annualRate,
        int termMonths,
        InterestRateMethod method,
        string currency,
        IReadOnlyList<ProductFee> fees,
        DateTimeOffset now,
        TimeSpan validity)
    {
        var schedule = AmortizationEngine.Generate(
            new LoanTerms(principal, annualRate, termMonths, method, currency, fees));
        return new LoanOffer(
            Guid.NewGuid(),
            applicationId,
            version,
            principal,
            annualRate,
            termMonths,
            method,
            currency,
            fees,
            schedule.Installments,
            now.Add(validity),
            OfferStatus.Issued,
            now,
            null,
            null);
    }

    public static LoanOffer Accept(LoanOffer offer, string acceptedBy, DateTimeOffset now)
    {
        if (offer.Status != OfferStatus.Issued)
        {
            throw new DomainException("Only an issued offer can be accepted.");
        }

        if (now > offer.ExpiresAt)
        {
            throw new DomainException("The offer has expired.");
        }

        return offer with { Status = OfferStatus.Accepted, AcceptedAt = now, AcceptedBy = acceptedBy };
    }

    public static LoanOffer ExpireIfNecessary(LoanOffer offer, DateTimeOffset now) =>
        offer.Status == OfferStatus.Issued && now > offer.ExpiresAt
            ? offer with { Status = OfferStatus.Expired }
            : offer;

    public static LoanOffer Counter(LoanOffer priorOffer, decimal principal, decimal annualRate, int termMonths, DateTimeOffset now)
    {
        if (priorOffer.Status == OfferStatus.Accepted)
        {
            throw new DomainException("An accepted offer is immutable and cannot be countered.");
        }

        var newOffer = Create(
            priorOffer.ApplicationId,
            priorOffer.Version + 1,
            principal,
            annualRate,
            termMonths,
            priorOffer.InterestMethod,
            priorOffer.Currency,
            priorOffer.Fees,
            now,
            priorOffer.ExpiresAt - priorOffer.CreatedAt);
        return newOffer;
    }
}

public static class AuditHashing
{
    public static string Hash(string? priorHash, string value)
    {
        var input = $"{priorHash ?? string.Empty}|{value}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
