using System.Security.Cryptography;
using System.Text;
using FieldOps.Domain;

namespace FieldOps.Application;

public sealed record FeatureFlagEvaluation(string Key, bool Enabled, string Reason);

public sealed class FeatureFlagService(
    ITenantContext tenantContext,
    IFeatureFlagStore store,
    IAppCache cache,
    IAuditWriter audit)
{
    public async Task<FeatureFlagEvaluation> EvaluateAsync(string key, Guid userId, CancellationToken cancellationToken)
    {
        var normalized = key.Trim().ToLowerInvariant();
        var flag = await store.FindAsync(normalized, cancellationToken);
        if (flag is null) return new(normalized, false, "flag-not-found");
        TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, flag);

        var cacheKey = $"flags:{normalized}:{userId:N}:v{flag.Version}";
        var cached = await cache.GetAsync<FeatureFlagEvaluation>(cacheKey, cancellationToken);
        if (cached is not null) return cached;

        FeatureFlagEvaluation result;
        if (flag.KillSwitch)
        {
            result = new(normalized, false, "kill-switch");
        }
        else
        {
            var userOverride = await store.FindOverrideAsync(normalized, userId, cancellationToken);
            if (userOverride is not null)
            {
                TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, userOverride);
                result = new(normalized, userOverride.Enabled, "user-override");
            }
            else if (!flag.Enabled)
            {
                result = new(normalized, false, "disabled");
            }
            else
            {
                var bucket = StableBucket(normalized, userId);
                result = new(normalized, bucket < flag.RolloutPercentage, $"rollout-bucket-{bucket}");
            }
        }

        await cache.SetAsync(cacheKey, result, TimeSpan.FromMinutes(2), cancellationToken);
        return result;
    }

    public async Task<FeatureFlag> UpsertAsync(
        string key,
        bool enabled,
        int rolloutPercentage,
        bool killSwitch,
        string actorId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var existing = await store.FindAsync(key, cancellationToken);
        object? before = existing is null
            ? null
            : new { existing.Enabled, existing.RolloutPercentage, existing.KillSwitch, existing.Version };
        var flag = existing ?? new FeatureFlag(tenantContext.RequiredTenantId, key, enabled, rolloutPercentage, killSwitch);
        if (existing is not null)
        {
            TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, existing);
            existing.Configure(enabled, rolloutPercentage, killSwitch);
        }

        await store.UpsertAsync(flag, cancellationToken);
        await store.SaveAsync(cancellationToken);
        await audit.WriteAsync(
            actorId, "feature-flag.changed", $"feature-flag/{flag.Key}", before,
            new { flag.Enabled, flag.RolloutPercentage, flag.KillSwitch, flag.Version },
            correlationId, null, null, cancellationToken);
        return flag;
    }

    public async Task<FeatureFlagOverride> SetUserOverrideAsync(
        string key,
        Guid userId,
        bool enabled,
        string actorId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var normalized = key.Trim().ToLowerInvariant();
        var flag = await store.FindAsync(normalized, cancellationToken)
            ?? throw new KeyNotFoundException("Feature flag was not found.");
        TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, flag);
        var existing = await store.FindOverrideAsync(normalized, userId, cancellationToken);
        var before = existing is null ? null : new { existing.Enabled };
        var value = existing ?? new FeatureFlagOverride(tenantContext.RequiredTenantId, normalized, userId, enabled);
        if (existing is not null)
        {
            TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, existing);
            existing.Set(enabled);
        }

        await store.UpsertOverrideAsync(value, cancellationToken);
        await store.SaveAsync(cancellationToken);
        await audit.WriteAsync(
            actorId,
            "feature-flag.user-override.changed",
            $"feature-flag/{normalized}/users/{userId:N}",
            before,
            new { value.Enabled },
            correlationId,
            null,
            null,
            cancellationToken);
        return value;
    }

    public static int StableBucket(string key, Guid userId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Trim().ToLowerInvariant()}:{userId:N}"));
        return BitConverter.ToUInt32(bytes, 0) % 100 is var value ? (int)value : 0;
    }
}
