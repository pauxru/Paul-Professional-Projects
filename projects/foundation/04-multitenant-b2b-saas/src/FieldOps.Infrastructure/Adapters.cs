using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FieldOps.Application;
using FieldOps.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class TenantCacheStore
{
    internal ConcurrentDictionary<string, CacheEntry> Entries { get; } = new(StringComparer.Ordinal);
    internal sealed record CacheEntry(object Value, DateTimeOffset ExpiresAt);
}

public sealed class TenantAwareMemoryCache(
    ITenantContext tenantContext,
    IClock clock,
    TenantCacheStore store) : IAppCache
{
    public ValueTask<T?> GetAsync<T>(string logicalKey, CancellationToken cancellationToken = default)
    {
        var key = ToPhysicalKey(logicalKey);
        if (!store.Entries.TryGetValue(key, out var entry)) return ValueTask.FromResult(default(T));
        if (entry.ExpiresAt <= clock.UtcNow)
        {
            store.Entries.TryRemove(key, out _);
            return ValueTask.FromResult(default(T));
        }

        return ValueTask.FromResult(entry.Value is T value ? value : default);
    }

    public ValueTask SetAsync<T>(
        string logicalKey,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        var key = ToPhysicalKey(logicalKey);
        AssertNamespaced(key);
        store.Entries[key] = new TenantCacheStore.CacheEntry(value, clock.UtcNow.Add(ttl));
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string logicalKey, CancellationToken cancellationToken = default)
    {
        store.Entries.TryRemove(ToPhysicalKey(logicalKey), out _);
        return ValueTask.CompletedTask;
    }

    public string ToPhysicalKey(string logicalKey)
    {
        if (string.IsNullOrWhiteSpace(logicalKey)) throw new ArgumentException("Cache key is required.", nameof(logicalKey));
        if (logicalKey.StartsWith("tenant:", StringComparison.Ordinal))
        {
            throw new CrossTenantAccessException("Callers must provide logical, not physical, cache keys.");
        }

        return $"tenant:{tenantContext.RequiredTenantId:N}:{logicalKey}";
    }

    public void AssertNamespaced(string physicalKey)
    {
        var prefix = $"tenant:{tenantContext.RequiredTenantId:N}:";
        if (!physicalKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new CrossTenantAccessException("An un-namespaced or foreign-tenant cache key was rejected.");
        }
    }
}

public sealed class LocalObjectStoreOptions
{
    public const string SectionName = "ObjectStore";
    public string RootPath { get; init; } = "App_Data\\objects";
    public long MaxBytes { get; init; } = 10 * 1024 * 1024;
}

public sealed class LocalObjectStore(
    ITenantContext tenantContext,
    LocalObjectStoreOptions options) : IObjectStore
{
    private static readonly HashSet<string> AllowedContentTypes =
        new(["image/jpeg", "image/png", "application/pdf"], StringComparer.OrdinalIgnoreCase);

    public async Task<StoredObject> PutAsync(
        string fileName,
        string contentType,
        Stream content,
        long length,
        CancellationToken cancellationToken)
    {
        if (length <= 0 || length > options.MaxBytes) throw new DomainRuleException("Attachment size is outside the allowed range.");
        if (!AllowedContentTypes.Contains(contentType)) throw new DomainRuleException("Attachment content type is not allowed.");

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) throw new DomainRuleException("Attachment file name is invalid.");
        var tenant = tenantContext.RequiredTenantId.ToString("N");
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Path.Combine(options.RootPath, tenant));
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, $"{id}-{safeName}"));
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenOperationException("Attachment path traversal was rejected.");
        }

        await using var target = File.Create(path);
        await content.CopyToAsync(target, cancellationToken);
        return new StoredObject($"obj://{tenant}/{id}-{safeName}", safeName, contentType, length);
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var expectedPrefix = $"obj://{tenantContext.RequiredTenantId:N}/";
        if (!objectKey.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new CrossTenantAccessException("Cross-tenant object access was rejected.");
        }

        var relative = objectKey[expectedPrefix.Length..];
        var directory = Path.GetFullPath(Path.Combine(options.RootPath, tenantContext.RequiredTenantId.ToString("N")));
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new FileNotFoundException("Object was not found.");
        }

        return Task.FromResult<Stream>(File.OpenRead(path));
    }
}

public sealed class BillingSimulatorOptions
{
    public const string SectionName = "Billing";
    public string WebhookSecret { get; init; } = "dev-only-webhook-secret-change-me";
    public int SignatureToleranceMinutes { get; init; } = 5;
    public int SuspendAfterFailures { get; init; } = 3;
}

public sealed class BillingProviderSimulator(
    FieldOpsDbContext dbContext,
    ITenantContext tenantContext,
    IClock clock,
    BillingSimulatorOptions options) : IBillingProvider
{
    private static readonly IReadOnlyDictionary<(SubscriptionPlan Plan, string Currency), decimal> Prices =
        new Dictionary<(SubscriptionPlan, string), decimal>
        {
            [(SubscriptionPlan.Free, "KES")] = 0,
            [(SubscriptionPlan.Starter, "KES")] = 4_900,
            [(SubscriptionPlan.Professional, "KES")] = 18_900,
            [(SubscriptionPlan.Enterprise, "KES")] = 75_000,
            [(SubscriptionPlan.Free, "USD")] = 0,
            [(SubscriptionPlan.Starter, "USD")] = 39,
            [(SubscriptionPlan.Professional, "USD")] = 149,
            [(SubscriptionPlan.Enterprise, "USD")] = 599
        };

    public async Task<BillingCustomerResult> CreateCustomerAsync(
        Guid tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        if (tenantId != tenantContext.RequiredTenantId) throw new CrossTenantAccessException("Billing tenant mismatch.");
        var id = $"cus_sim_{Guid.NewGuid():N}";
        await dbContext.BillingCustomers.AddAsync(new BillingCustomer(tenantId, id, clock.UtcNow), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new BillingCustomerResult(id);
    }

    public async Task<BillingSubscriptionResult> CreateSubscriptionAsync(
        Guid tenantId,
        SubscriptionPlan plan,
        string currency,
        CancellationToken cancellationToken)
    {
        if (tenantId != tenantContext.RequiredTenantId) throw new CrossTenantAccessException("Billing tenant mismatch.");
        var normalizedCurrency = NormalizeCurrency(currency);
        var id = $"sub_sim_{Guid.NewGuid():N}";
        var periodStart = clock.UtcNow;
        var periodEnd = periodStart.AddMonths(1);
        await dbContext.BillingSubscriptions.AddAsync(
            new BillingSubscription(tenantId, id, plan, normalizedCurrency, periodStart, periodEnd),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new BillingSubscriptionResult(id, periodStart, periodEnd);
    }

    public Task<ProrationPreview> PreviewPlanChangeAsync(
        SubscriptionPlan current,
        SubscriptionPlan target,
        string currency,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var normalizedCurrency = NormalizeCurrency(currency);
        var daysInMonth = DateTime.DaysInMonth(at.Year, at.Month);
        var remainingFraction = Math.Max(0m, (daysInMonth - at.Day + 1m) / daysInMonth);
        var credit = Prices[(current, normalizedCurrency)] * remainingFraction;
        var charge = Prices[(target, normalizedCurrency)] * remainingFraction;
        return Task.FromResult(new ProrationPreview(
            new Money(credit, normalizedCurrency).Normalize(),
            new Money(charge, normalizedCurrency).Normalize(),
            new Money(Math.Max(0, charge - credit), normalizedCurrency).Normalize()));
    }

    public async Task ChangePlanAsync(
        string providerSubscriptionId,
        SubscriptionPlan target,
        CancellationToken cancellationToken)
    {
        var subscription = await dbContext.BillingSubscriptions.SingleOrDefaultAsync(
            x => x.ProviderSubscriptionId == providerSubscriptionId,
            cancellationToken) ?? throw new KeyNotFoundException("Subscription was not found.");
        subscription.ChangePlan(target);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public SignedWebhook CreateSignedWebhook(BillingWebhookMessage message, DateTimeOffset timestamp)
    {
        var body = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var unix = timestamp.ToUnixTimeSeconds();
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.WebhookSecret),
            Encoding.UTF8.GetBytes($"{unix}.{body}"));
        return new SignedWebhook(body, $"t={unix},v1={Convert.ToHexString(signature).ToLowerInvariant()}");
    }

    private static string NormalizeCurrency(string currency)
    {
        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized is not ("KES" or "USD")) throw new DomainRuleException("Currency must be KES or USD.");
        return normalized;
    }
}

public sealed class AuditWriter(
    FieldOpsDbContext dbContext,
    ITenantContext tenantContext,
    IClock clock) : IAuditWriter
{
    public async Task WriteAsync(
        string actorId,
        string action,
        string resource,
        object? before,
        object? after,
        string correlationId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var entry = new AuditEntry(
            tenantContext.RequiredTenantId,
            actorId,
            action,
            resource,
            AuditHash.Of(before),
            AuditHash.Of(after),
            correlationId,
            ipAddress,
            userAgent,
            clock.UtcNow);
        await dbContext.AuditEntries.AddAsync(entry, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
