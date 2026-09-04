using FieldOps.Domain;

namespace FieldOps.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ITenantContext
{
    Guid? TenantId { get; }
    string? TenantSlug { get; }
    bool IsResolved { get; }
    Guid RequiredTenantId { get; }
}

public interface IMutableTenantContext : ITenantContext
{
    void Set(Guid tenantId, string slug);
}

public interface IJobRepository
{
    Task<Job?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Job>> ListAsync(int skip, int take, JobStatus? status, string? sort, CancellationToken cancellationToken);
    Task<int> CountAsync(JobStatus? status, CancellationToken cancellationToken);
    Task AddAsync(Job job, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IAssetRepository
{
    Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<int> CountAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Asset>> ListAsync(int skip, int take, CancellationToken cancellationToken);
    Task AddAsync(Asset asset, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IOrganizationRepository
{
    Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken);
    Task<IReadOnlyList<Organization>> ListAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IMembershipRepository
{
    Task<Membership?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<Membership?> FindByUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> UserBelongsToTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
    Task<int> CountActiveAsync(CancellationToken cancellationToken);
    Task AddAsync(Membership membership, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IInvitationRepository
{
    Task<Invitation?> FindByTokenAsync(string rawToken, CancellationToken cancellationToken);
    Task AddAsync(Invitation invitation, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IInspectionRepository
{
    Task<InspectionTemplate?> FindTemplateAsync(Guid id, CancellationToken cancellationToken);
    Task AddTemplateAsync(InspectionTemplate template, CancellationToken cancellationToken);
    Task AddSubmissionAsync(InspectionSubmission submission, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IUsageMeterStore
{
    Task<long> IncrementAsync(Guid tenantId, string metric, DateOnly periodStart, long amount, CancellationToken cancellationToken);
    Task<long> GetAsync(Guid tenantId, string metric, DateOnly periodStart, CancellationToken cancellationToken);
}

public interface IFeatureFlagStore
{
    Task<FeatureFlag?> FindAsync(string key, CancellationToken cancellationToken);
    Task<FeatureFlagOverride?> FindOverrideAsync(string key, Guid userId, CancellationToken cancellationToken);
    Task UpsertAsync(FeatureFlag flag, CancellationToken cancellationToken);
    Task UpsertOverrideAsync(FeatureFlagOverride value, CancellationToken cancellationToken);
    Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IAuditWriter
{
    Task WriteAsync(
        string actorId,
        string action,
        string resource,
        object? before,
        object? after,
        string correlationId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);
}

public interface IAppCache
{
    ValueTask<T?> GetAsync<T>(string logicalKey, CancellationToken cancellationToken = default);
    ValueTask SetAsync<T>(string logicalKey, T value, TimeSpan ttl, CancellationToken cancellationToken = default);
    ValueTask RemoveAsync(string logicalKey, CancellationToken cancellationToken = default);
    string ToPhysicalKey(string logicalKey);
    void AssertNamespaced(string physicalKey);
}

public interface IObjectStore
{
    Task<StoredObject> PutAsync(
        string fileName,
        string contentType,
        Stream content,
        long length,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed record StoredObject(string ObjectKey, string FileName, string ContentType, long Length);

public interface IBillingProvider
{
    Task<BillingCustomerResult> CreateCustomerAsync(Guid tenantId, string name, CancellationToken cancellationToken);
    Task<BillingSubscriptionResult> CreateSubscriptionAsync(Guid tenantId, SubscriptionPlan plan, string currency, CancellationToken cancellationToken);
    Task<ProrationPreview> PreviewPlanChangeAsync(SubscriptionPlan current, SubscriptionPlan target, string currency, DateTimeOffset at, CancellationToken cancellationToken);
    Task ChangePlanAsync(string providerSubscriptionId, SubscriptionPlan target, CancellationToken cancellationToken);
    SignedWebhook CreateSignedWebhook(BillingWebhookMessage message, DateTimeOffset timestamp);
}

public interface IWebhookReceiptStore
{
    Task<bool> TryRecordAsync(string eventId, string eventType, DateTimeOffset processedAt, CancellationToken cancellationToken);
}

public sealed record BillingCustomerResult(string ProviderCustomerId);
public sealed record BillingSubscriptionResult(string ProviderSubscriptionId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);
public sealed record ProrationPreview(Money Credit, Money Charge, Money Net);
public sealed record BillingWebhookMessage(string EventId, BillingEventType Type, Guid TenantId, string? ProviderSubscriptionId = null);
public sealed record SignedWebhook(string RawBody, string Signature);

public sealed class CrossTenantAccessException(string message) : Exception(message);
public sealed class TenantNotResolvedException() : Exception("A tenant context is required for this operation.");
public sealed class ForbiddenOperationException(string message) : Exception(message);

public sealed class EntitlementDeniedException(string feature, SubscriptionPlan plan)
    : Exception($"Feature '{feature}' is not available on the {plan} plan.")
{
    public string Feature { get; } = feature;
    public SubscriptionPlan Plan { get; } = plan;
}

public sealed class QuotaExceededException(string metric, long limit, bool rateLimit)
    : Exception($"The {metric} quota of {limit} has been exceeded.")
{
    public string Metric { get; } = metric;
    public long Limit { get; } = limit;
    public bool RateLimit { get; } = rateLimit;
}
