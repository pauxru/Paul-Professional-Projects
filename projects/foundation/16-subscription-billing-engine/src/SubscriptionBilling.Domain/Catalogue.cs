namespace SubscriptionBilling.Domain;

public sealed class Product
{
    public Product(Guid id, string name, string description)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Product id is required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name.Trim();
        Description = description?.Trim() ?? string.Empty;
        IsActive = true;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsActive { get; private set; }

    public void Archive() => IsActive = false;
}

public sealed record PlanVersion(
    Guid Id,
    Guid PlanId,
    int Version,
    DateTimeOffset EffectiveFrom,
    string Currency,
    PricingConfiguration Pricing,
    bool TaxInclusive)
{
    public IPricingStrategy CreatePricingStrategy() =>
        PricingStrategyFactory.Create(Pricing, Currency);
}

public sealed class Plan
{
    private readonly List<PlanVersion> _versions = [];

    public Plan(
        Guid id,
        Guid productId,
        string name,
        BillingInterval interval,
        Guid? meterId = null)
    {
        if (id == Guid.Empty || productId == Guid.Empty)
        {
            throw new DomainException("Plan and product ids are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        ProductId = productId;
        Name = name.Trim();
        Interval = interval;
        MeterId = meterId;
    }

    public Guid Id { get; }
    public Guid ProductId { get; }
    public string Name { get; }
    public BillingInterval Interval { get; }
    public Guid? MeterId { get; }
    public IReadOnlyList<PlanVersion> Versions => _versions.AsReadOnly();

    public PlanVersion AddVersion(
        Guid id,
        DateTimeOffset effectiveFrom,
        string currency,
        PricingConfiguration pricing,
        bool taxInclusive)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Plan version id is required.");
        }

        if (_versions.Any(version => version.EffectiveFrom == effectiveFrom))
        {
            throw new DomainException("Only one plan version may become effective at a given instant.");
        }

        var version = new PlanVersion(
            id,
            Id,
            _versions.Count + 1,
            effectiveFrom,
            Money.Zero(currency).Currency,
            pricing,
            taxInclusive);
        _ = version.CreatePricingStrategy();
        _versions.Add(version);
        return version;
    }

    public PlanVersion VersionEffectiveAt(DateTimeOffset instant)
    {
        var version = _versions
            .Where(candidate => candidate.EffectiveFrom <= instant)
            .OrderByDescending(candidate => candidate.EffectiveFrom)
            .FirstOrDefault();
        return version ?? throw new DomainException("No plan version is effective at that instant.");
    }
}
