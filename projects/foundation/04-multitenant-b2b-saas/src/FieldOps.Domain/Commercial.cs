namespace FieldOps.Domain;

public sealed class UsageCounter : ITenantOwned
{
    private UsageCounter()
    {
    }

    public UsageCounter(Guid tenantId, string metric, DateOnly periodStart)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Metric = metric;
        PeriodStart = periodStart;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string Metric { get; private set; } = string.Empty;
    public DateOnly PeriodStart { get; private set; }
    public long Value { get; private set; }
    public long Version { get; private set; }

    public long Increment(long amount)
    {
        if (amount <= 0) throw new DomainRuleException("Usage increment must be positive.");
        Value += amount;
        Version++;
        return Value;
    }
}

public sealed class FeatureFlag : ITenantOwned
{
    private FeatureFlag()
    {
    }

    public FeatureFlag(Guid tenantId, string key, bool enabled, int rolloutPercentage, bool killSwitch)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Key = NormalizeKey(key);
        Configure(enabled, rolloutPercentage, killSwitch);
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string Key { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    public int RolloutPercentage { get; private set; }
    public bool KillSwitch { get; private set; }
    public long Version { get; private set; }

    public void Configure(bool enabled, int rolloutPercentage, bool killSwitch)
    {
        if (rolloutPercentage is < 0 or > 100)
        {
            throw new DomainRuleException("Rollout percentage must be between 0 and 100.");
        }

        Enabled = enabled;
        RolloutPercentage = rolloutPercentage;
        KillSwitch = killSwitch;
        Version++;
    }

    private static string NormalizeKey(string key) =>
        string.IsNullOrWhiteSpace(key)
            ? throw new DomainRuleException("Feature flag key is required.")
            : key.Trim().ToLowerInvariant();
}

public sealed class FeatureFlagOverride : ITenantOwned
{
    private FeatureFlagOverride()
    {
    }

    public FeatureFlagOverride(Guid tenantId, string flagKey, Guid userId, bool enabled)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        FlagKey = flagKey.Trim().ToLowerInvariant();
        UserId = userId;
        Enabled = enabled;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string FlagKey { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
    public bool Enabled { get; private set; }

    public void Set(bool enabled) => Enabled = enabled;
}

public sealed class BillingCustomer : ITenantOwned
{
    private BillingCustomer()
    {
    }

    public BillingCustomer(Guid tenantId, string providerCustomerId, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        ProviderCustomerId = providerCustomerId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string ProviderCustomerId { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class BillingSubscription : ITenantOwned
{
    private BillingSubscription()
    {
    }

    public BillingSubscription(Guid tenantId, string providerSubscriptionId, SubscriptionPlan plan, string currency, DateTimeOffset periodStart, DateTimeOffset periodEnd)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        ProviderSubscriptionId = providerSubscriptionId;
        Plan = plan;
        Currency = currency.ToUpperInvariant();
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Active = true;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string ProviderSubscriptionId { get; private set; } = string.Empty;
    public SubscriptionPlan Plan { get; private set; }
    public string Currency { get; private set; } = "KES";
    public DateTimeOffset PeriodStart { get; private set; }
    public DateTimeOffset PeriodEnd { get; private set; }
    public bool Active { get; private set; }

    public void ChangePlan(SubscriptionPlan plan) => Plan = plan;
}

public sealed class WebhookReceipt
{
    private WebhookReceipt()
    {
    }

    public WebhookReceipt(string eventId, string eventType, DateTimeOffset processedAt)
    {
        EventId = eventId;
        EventType = eventType;
        ProcessedAt = processedAt;
    }

    public string EventId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }
}

public sealed class AuditEntry : ITenantOwned, IAppendOnly
{
    private AuditEntry()
    {
    }

    public AuditEntry(
        Guid tenantId,
        string actorId,
        string action,
        string resource,
        string? beforeHash,
        string? afterHash,
        string correlationId,
        string? ipAddress,
        string? userAgent,
        DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        ActorId = actorId;
        Action = action;
        Resource = resource;
        BeforeHash = beforeHash;
        AfterHash = afterHash;
        CorrelationId = correlationId;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string ActorId { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string Resource { get; private set; } = string.Empty;
    public string? BeforeHash { get; private set; }
    public string? AfterHash { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}
