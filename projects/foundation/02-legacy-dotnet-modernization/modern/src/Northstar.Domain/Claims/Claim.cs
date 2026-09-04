using Northstar.Domain.Common;

namespace Northstar.Domain.Claims;

public sealed class Claim
{
    private readonly List<ClaimDocument> _documents = [];

    private Claim()
    {
    }

    private Claim(
        Guid id,
        Guid policyId,
        string reference,
        decimal claimedAmount,
        string currency,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || policyId == Guid.Empty)
        {
            throw new DomainRuleException("A claim requires an identity and policy.");
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new DomainRuleException("A claim reference is required.");
        }

        if (claimedAmount <= 0)
        {
            throw new DomainRuleException("Claimed amount must be greater than zero.");
        }

        Id = id;
        PolicyId = policyId;
        Reference = reference.Trim().ToUpperInvariant();
        ClaimedAmount = claimedAmount;
        Currency = new Money(0m, currency).Currency;
        Status = ClaimStatus.Submitted;
        CreatedAt = createdAt;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid PolicyId { get; private set; }
    public string Reference { get; private set; } = string.Empty;
    public decimal ClaimedAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public decimal ReserveAmount { get; private set; }
    public decimal SettlementAmount { get; private set; }
    public ClaimStatus Status { get; private set; }
    public string? AssignedAdjuster { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyCollection<ClaimDocument> Documents => _documents.AsReadOnly();

    public static Claim Create(
        Guid id,
        Guid policyId,
        string reference,
        decimal claimedAmount,
        string currency,
        DateTimeOffset createdAt) =>
        new(id, policyId, reference, claimedAmount, currency, createdAt);

    public static Claim Rehydrate(
        Guid id,
        Guid policyId,
        string reference,
        decimal claimedAmount,
        string currency,
        decimal reserveAmount,
        decimal settlementAmount,
        ClaimStatus status,
        string? assignedAdjuster,
        int version,
        DateTimeOffset createdAt)
    {
        var claim = new Claim(id, policyId, reference, claimedAmount, currency, createdAt)
        {
            ReserveAmount = reserveAmount,
            SettlementAmount = settlementAmount,
            Status = status,
            AssignedAdjuster = assignedAdjuster,
            Version = Math.Max(version, 1)
        };
        return claim;
    }

    public void StartReview()
    {
        TransitionTo(ClaimStatus.UnderReview);
    }

    public void AssignAdjuster(string adjuster)
    {
        if (Status != ClaimStatus.UnderReview)
        {
            throw new DomainRuleException("An adjuster can only be assigned while a claim is under review.");
        }

        if (string.IsNullOrWhiteSpace(adjuster))
        {
            throw new DomainRuleException("An adjuster name is required.");
        }

        AssignedAdjuster = adjuster.Trim();
        IncrementVersion();
    }

    public void SetReserve(decimal reserveAmount)
    {
        if (reserveAmount < 0)
        {
            throw new DomainRuleException("Reserve amount cannot be negative.");
        }

        ReserveAmount = reserveAmount;
        IncrementVersion();
    }

    public void TransitionTo(ClaimStatus nextStatus)
    {
        if (!IsAllowedTransition(Status, nextStatus))
        {
            throw new DomainRuleException($"Transition from {Status} to {nextStatus} is not allowed.");
        }

        Status = nextStatus;
        IncrementVersion();
    }

    public void RecordSettlement(decimal settlementAmount)
    {
        if (Status != ClaimStatus.Settled)
        {
            throw new DomainRuleException("A settlement can only be recorded after the claim is settled.");
        }

        if (settlementAmount < 0)
        {
            throw new DomainRuleException("Settlement amount cannot be negative.");
        }

        SettlementAmount = settlementAmount;
        IncrementVersion();
    }

    public void AddDocument(ClaimDocument document)
    {
        if (document.ClaimId != Id)
        {
            throw new DomainRuleException("A document must belong to its claim.");
        }

        _documents.Add(document);
        IncrementVersion();
    }

    public static bool IsAllowedTransition(ClaimStatus from, ClaimStatus to) => (from, to) switch
    {
        (ClaimStatus.Submitted, ClaimStatus.UnderReview) => true,
        (ClaimStatus.UnderReview, ClaimStatus.Approved) => true,
        (ClaimStatus.UnderReview, ClaimStatus.Rejected) => true,
        (ClaimStatus.Approved, ClaimStatus.Settled) => true,
        (ClaimStatus.Approved, ClaimStatus.Closed) => true,
        (ClaimStatus.Rejected, ClaimStatus.Closed) => true,
        (ClaimStatus.Settled, ClaimStatus.Closed) => true,
        _ => false
    };

    private void IncrementVersion() => Version++;
}
