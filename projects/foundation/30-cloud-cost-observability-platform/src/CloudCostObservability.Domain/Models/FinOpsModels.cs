using System.Text.Json;

namespace CloudCostObservability.Domain.Models;

public enum ResourceCategory { Compute, Storage, Database, Networking, Serverless, Ai, Monitoring }
public enum CostBasis { Actual, Amortized }
public enum AllocationMethod { DirectTag, ResourceGroupMapping, ParentInheritance, SharedProportional, SharedEven, SharedFixed, Unallocated }
public enum SharedSplitStrategy { ProportionalUsage, Even, FixedPercentage }
public enum BudgetScope { Team, Subscription, Tag, Environment }
public enum BudgetCadence { Monthly, Custom }
public enum ForecastMethod { RunRate, SeasonalAware, LinearRegression }
public enum AnomalyDetector { RollingMad, Ewma, Cusum }
public enum AnomalySeverity { Low, Medium, High, Critical }
public enum RecommendationType { IdleCompute, OrphanedResource, OversizedSku, NonProductionSchedule, StorageTiering, CommitmentPurchase, UntaggedResource }
public enum RecommendationLifecycle { Open, Accepted, Implemented, Verified, Dismissed }
public enum ConfidenceLevel { Low, Medium, High }
public enum ImportProvider { Synthetic, AzureCostManagementExport, AwsCur }

public readonly record struct Money
{
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a three-letter ISO code.", nameof(currency));
        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public decimal Amount { get; }
    public string Currency { get; }

    public static Money operator +(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot add amounts in different currencies.");
        return new Money(left.Amount + right.Amount, left.Currency);
    }
}

public sealed class CloudResource
{
    private CloudResource() { }

    public CloudResource(
        string id,
        string name,
        string resourceType,
        ResourceCategory category,
        string region,
        string subscriptionId,
        string resourceGroup,
        string sku,
        DateOnly createdOn,
        IReadOnlyDictionary<string, string>? tags = null,
        string? owner = null,
        string? parentResourceId = null,
        DateOnly? deletedOn = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A resource id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(subscriptionId)) throw new ArgumentException("A subscription is required.", nameof(subscriptionId));
        Id = id;
        Name = name;
        ResourceType = resourceType;
        Category = category;
        Region = region;
        SubscriptionId = subscriptionId;
        ResourceGroup = resourceGroup;
        Sku = sku;
        CreatedOn = createdOn;
        Owner = owner;
        ParentResourceId = parentResourceId;
        DeletedOn = deletedOn;
        SetTags(tags ?? new Dictionary<string, string>());
    }

    public string Id { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string ResourceType { get; private set; } = string.Empty;
    public ResourceCategory Category { get; private set; }
    public string Region { get; private set; } = string.Empty;
    public string SubscriptionId { get; private set; } = string.Empty;
    public string ResourceGroup { get; private set; } = string.Empty;
    public string Sku { get; private set; } = string.Empty;
    public DateOnly CreatedOn { get; private set; }
    public DateOnly? DeletedOn { get; private set; }
    public string? Owner { get; private set; }
    public string? ParentResourceId { get; private set; }
    public string TagsJson { get; private set; } = "{}";
    public int Version { get; private set; } = 1;

    public IReadOnlyDictionary<string, string> Tags =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(TagsJson) ?? new Dictionary<string, string>();

    public bool IsActiveOn(DateOnly date) => CreatedOn <= date && (DeletedOn is null || DeletedOn >= date);

    public void SetTags(IReadOnlyDictionary<string, string> tags)
    {
        TagsJson = JsonSerializer.Serialize(tags.ToDictionary(
            x => x.Key.Trim().ToLowerInvariant(),
            x => x.Value.Trim(),
            StringComparer.OrdinalIgnoreCase));
        Version++;
    }

    public void SetParent(string? parentResourceId)
    {
        ParentResourceId = parentResourceId;
        Version++;
    }
}

public sealed class CostRecord
{
    private CostRecord() { }

    public CostRecord(
        string billingPeriod,
        string resourceId,
        DateOnly usageDate,
        string meter,
        string service,
        ResourceCategory category,
        decimal usageQuantity,
        string unit,
        decimal rate,
        decimal actualCost,
        decimal amortizedCost,
        decimal credits = 0,
        decimal discounts = 0,
        decimal reservedCoveragePercent = 0,
        decimal reservedUtilizationPercent = 0,
        bool isHourly = false,
        int? hour = null,
        string currency = "USD",
        DateTimeOffset? importedAt = null)
    {
        if (usageQuantity < 0 || rate < 0) throw new ArgumentOutOfRangeException(nameof(usageQuantity));
        if (actualCost < 0 || amortizedCost < 0) throw new ArgumentOutOfRangeException(nameof(actualCost));
        if (reservedCoveragePercent is < 0 or > 100 || reservedUtilizationPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(reservedCoveragePercent));
        BillingPeriod = billingPeriod;
        ResourceId = resourceId;
        UsageDate = usageDate;
        Meter = meter;
        Service = service;
        Category = category;
        UsageQuantity = usageQuantity;
        Unit = unit;
        Rate = rate;
        ActualCost = actualCost;
        AmortizedCost = amortizedCost;
        Credits = credits;
        Discounts = discounts;
        ReservedCoveragePercent = reservedCoveragePercent;
        ReservedUtilizationPercent = reservedUtilizationPercent;
        IsHourly = isHourly;
        Hour = hour;
        Currency = currency;
        IdempotencyKey = BuildIdempotencyKey(billingPeriod, resourceId, meter, usageDate, hour);
        Id = IdempotencyKey;
        ImportedAt = importedAt ?? DateTimeOffset.UnixEpoch;
    }

    public string Id { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string BillingPeriod { get; private set; } = string.Empty;
    public string ResourceId { get; private set; } = string.Empty;
    public DateOnly UsageDate { get; private set; }
    public int? Hour { get; private set; }
    public bool IsHourly { get; private set; }
    public string Meter { get; private set; } = string.Empty;
    public string Service { get; private set; } = string.Empty;
    public ResourceCategory Category { get; private set; }
    public decimal UsageQuantity { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public decimal Rate { get; private set; }
    public decimal ActualCost { get; private set; }
    public decimal AmortizedCost { get; private set; }
    public decimal Credits { get; private set; }
    public decimal Discounts { get; private set; }
    public decimal ReservedCoveragePercent { get; private set; }
    public decimal ReservedUtilizationPercent { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateTimeOffset ImportedAt { get; private set; } = DateTimeOffset.UnixEpoch;

    public decimal EffectiveActualCost => ActualCost - Credits - Discounts;
    public decimal Cost(CostBasis basis) => basis == CostBasis.Actual ? EffectiveActualCost : AmortizedCost - Credits - Discounts;

    public void RestateFrom(CostRecord newer)
    {
        if (!string.Equals(IdempotencyKey, newer.IdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("A restatement must retain its idempotency key.");
        UsageQuantity = newer.UsageQuantity;
        Unit = newer.Unit;
        Rate = newer.Rate;
        ActualCost = newer.ActualCost;
        AmortizedCost = newer.AmortizedCost;
        Credits = newer.Credits;
        Discounts = newer.Discounts;
        ReservedCoveragePercent = newer.ReservedCoveragePercent;
        ReservedUtilizationPercent = newer.ReservedUtilizationPercent;
        ImportedAt = newer.ImportedAt;
    }

    public static string BuildIdempotencyKey(string billingPeriod, string resourceId, string meter, DateOnly usageDate, int? hour = null) =>
        $"{billingPeriod}|{resourceId}|{meter}|{usageDate:yyyy-MM-dd}|{hour?.ToString() ?? "daily"}";
}

public sealed class FxRate
{
    private FxRate() { }
    public FxRate(DateOnly rateDate, string fromCurrency, string toCurrency, decimal rate)
    {
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        Id = $"{rateDate:yyyyMMdd}|{fromCurrency}|{toCurrency}";
        RateDate = rateDate;
        FromCurrency = fromCurrency.ToUpperInvariant();
        ToCurrency = toCurrency.ToUpperInvariant();
        Rate = rate;
    }

    public string Id { get; private set; } = string.Empty;
    public DateOnly RateDate { get; private set; }
    public string FromCurrency { get; private set; } = "USD";
    public string ToCurrency { get; private set; } = "USD";
    public decimal Rate { get; private set; }
    public Money Convert(Money amount) =>
        !string.Equals(amount.Currency, FromCurrency, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException("Source currency does not match rate.")
            : new Money(decimal.Round(amount.Amount * Rate, 4, MidpointRounding.AwayFromZero), ToCurrency);
}

public sealed record AllocationRule(
    int Order,
    AllocationMethod Method,
    string? MatchKey = null,
    string? MatchValue = null,
    string? TargetTeam = null,
    string? TargetCostCentre = null,
    SharedSplitStrategy? SplitStrategy = null,
    IReadOnlyDictionary<string, decimal>? FixedPercentages = null,
    bool Enabled = true);

public sealed record AllocationLine(
    string CostRecordId,
    DateOnly UsageDate,
    string Team,
    string CostCentre,
    decimal Amount,
    AllocationMethod Method,
    string Explanation,
    string? RuleReference = null);

public sealed record AllocationAudit(
    string CostRecordId,
    decimal SourceAmount,
    decimal AllocatedAmount,
    decimal UnallocatedAmount,
    IReadOnlyList<AllocationLine> Lines);

public sealed class BudgetDefinition
{
    private BudgetDefinition() { }
    public BudgetDefinition(string id, BudgetScope scope, string selector, decimal amount, DateOnly periodStart, DateOnly periodEnd, BudgetCadence cadence = BudgetCadence.Monthly)
    {
        if (amount <= 0 || periodEnd < periodStart) throw new ArgumentOutOfRangeException(nameof(amount));
        Id = id;
        Scope = scope;
        Selector = selector;
        Amount = amount;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Cadence = cadence;
    }

    public string Id { get; private set; } = string.Empty;
    public BudgetScope Scope { get; private set; }
    public string Selector { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }
    public BudgetCadence Cadence { get; private set; }
    public string AlertStateJson { get; private set; } = "[]";

    public void RecordAlertState(IEnumerable<int> thresholds) => AlertStateJson = JsonSerializer.Serialize(thresholds.Distinct().Order());
    public IReadOnlySet<int> AlertedThresholds => (JsonSerializer.Deserialize<int[]>(AlertStateJson) ?? []).ToHashSet();
}

public sealed record BudgetStatus(string BudgetId, decimal BudgetAmount, decimal Spend, decimal PercentUsed, IReadOnlyList<int> NewlyCrossedThresholds);

public sealed record ForecastResult(
    ForecastMethod Method,
    decimal ProjectedMonthEnd,
    decimal LowerBound,
    decimal UpperBound,
    decimal MeasuredMape,
    bool IsDefault);

public sealed record ForecastBacktest(ForecastMethod Method, decimal MapePercent, int MonthsEvaluated);

public sealed class AnomalyEvent
{
    private AnomalyEvent() { }
    public AnomalyEvent(string id, DateOnly detectedOn, string grain, string dimension, decimal observed, decimal expected, decimal severityScore, AnomalySeverity severity, AnomalyDetector detector, string explanation, string groupKey)
    {
        Id = id;
        DetectedOn = detectedOn;
        Grain = grain;
        Dimension = dimension;
        Observed = observed;
        Expected = expected;
        SeverityScore = severityScore;
        Severity = severity;
        Detector = detector;
        Explanation = explanation;
        GroupKey = groupKey;
    }

    public string Id { get; private set; } = string.Empty;
    public DateOnly DetectedOn { get; private set; }
    public string Grain { get; private set; } = string.Empty;
    public string Dimension { get; private set; } = string.Empty;
    public decimal Observed { get; private set; }
    public decimal Expected { get; private set; }
    public decimal SeverityScore { get; private set; }
    public AnomalySeverity Severity { get; private set; }
    public AnomalyDetector Detector { get; private set; }
    public string Explanation { get; private set; } = string.Empty;
    public string GroupKey { get; private set; } = string.Empty;
    public bool Acknowledged { get; private set; }
    public bool Suppressed { get; private set; }
    public void Acknowledge() => Acknowledged = true;
    public void Suppress() => Suppressed = true;
}

public sealed class Recommendation
{
    private Recommendation() { }
    public Recommendation(string id, RecommendationType type, string resourceId, string title, decimal projectedMonthlySavings, ConfidenceLevel confidence, string evidence, string remediationSteps)
    {
        if (projectedMonthlySavings < 0) throw new ArgumentOutOfRangeException(nameof(projectedMonthlySavings));
        Id = id;
        Type = type;
        ResourceId = resourceId;
        Title = title;
        ProjectedMonthlySavings = projectedMonthlySavings;
        Confidence = confidence;
        Evidence = evidence;
        RemediationSteps = remediationSteps;
    }

    public string Id { get; private set; } = string.Empty;
    public RecommendationType Type { get; private set; }
    public string ResourceId { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public decimal ProjectedMonthlySavings { get; private set; }
    public ConfidenceLevel Confidence { get; private set; }
    public string Evidence { get; private set; } = string.Empty;
    public string RemediationSteps { get; private set; } = string.Empty;
    public RecommendationLifecycle Lifecycle { get; private set; } = RecommendationLifecycle.Open;
    public decimal? BaselineMonthlyCost { get; private set; }
    public decimal? RealisedMonthlySavings { get; private set; }

    public void Accept()
    {
        if (Lifecycle != RecommendationLifecycle.Open) throw new InvalidOperationException("Only open recommendations can be accepted.");
        Lifecycle = RecommendationLifecycle.Accepted;
    }

    public void Implement(decimal baselineMonthlyCost)
    {
        if (Lifecycle != RecommendationLifecycle.Accepted) throw new InvalidOperationException("Only accepted recommendations can be implemented.");
        if (baselineMonthlyCost < 0) throw new ArgumentOutOfRangeException(nameof(baselineMonthlyCost));
        BaselineMonthlyCost = baselineMonthlyCost;
        Lifecycle = RecommendationLifecycle.Implemented;
    }

    public void Verify(decimal postChangeMonthlyCost)
    {
        if (Lifecycle != RecommendationLifecycle.Implemented) throw new InvalidOperationException("Only implemented recommendations can be verified.");
        if (postChangeMonthlyCost < 0) throw new ArgumentOutOfRangeException(nameof(postChangeMonthlyCost));
        RealisedMonthlySavings = Math.Max(0, (BaselineMonthlyCost ?? 0) - postChangeMonthlyCost);
        Lifecycle = RecommendationLifecycle.Verified;
    }

    public void Dismiss()
    {
        if (Lifecycle is RecommendationLifecycle.Verified or RecommendationLifecycle.Implemented)
            throw new InvalidOperationException("Implemented or verified recommendations cannot be dismissed.");
        Lifecycle = RecommendationLifecycle.Dismissed;
    }
}

public sealed record BusinessMetric(DateOnly Date, string Team, int Orders, int ActiveTenants, decimal GigabytesProcessed);
public sealed record UnitEconomicsPoint(DateOnly Date, string Team, decimal CostPerOrder, decimal CostPerActiveTenant, decimal CostPerGigabyte, decimal Cost, int Orders, int ActiveTenants, decimal GigabytesProcessed);
public sealed record PlannedSuppression(DateOnly Start, DateOnly End, string? Dimension, string Reason)
{
    public bool AppliesTo(DateOnly date, string dimension) => date >= Start && date <= End && (Dimension is null || string.Equals(Dimension, dimension, StringComparison.OrdinalIgnoreCase));
}
