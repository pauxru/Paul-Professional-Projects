using System.ComponentModel.DataAnnotations;

namespace Contoso.Storefront.Application.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; init; } = "Sqlite";

    [Required]
    public string ConnectionString { get; init; } = "Data Source=storefront.db";
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public const string DevelopmentSigningKey = "dev-only-not-a-real-secret-change-me-0123456789";

    [Required]
    public string Issuer { get; init; } = "contoso-storefront-local";

    [Required]
    public string Audience { get; init; } = "contoso-storefront-api";

    [Required]
    [MinLength(32)]
    public string SigningKey { get; init; } = DevelopmentSigningKey;

    public string? Authority { get; init; }
}

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    [Required]
    public string Provider { get; init; } = "Memory";

    public string? ConnectionString { get; init; }
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    [Required]
    public string Provider { get; init; } = "InMemory";

    public string? ConnectionString { get; init; }
    public string? FullyQualifiedNamespace { get; init; }
    public string QueueName { get; init; } = "storefront-orders";
}

public sealed class KeyVaultOptions
{
    public const string SectionName = "KeyVault";
    public bool Enabled { get; init; }
    public string? VaultUri { get; init; }
    public string SecretNamePrefix { get; init; } = "storefront--";
}

public sealed class OperationalOptions
{
    public const string SectionName = "Operations";

    [Range(0, 300)]
    public int PreStopDelaySeconds { get; init; } = 1;

    [Range(1, 600)]
    public int DrainTimeoutSeconds { get; init; } = 30;

    [Range(1, 20)]
    public int DependencyWaitMaxAttempts { get; init; } = 5;

    [Range(0, 30_000)]
    public int DependencyWaitInitialDelayMs { get; init; } = 100;

    [Range(1, 500)]
    public int WorkerPollIntervalMs { get; init; } = 100;

    [Range(1, 100)]
    public int WorkerBatchSize { get; init; } = 20;
}

public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    [Range(1, 120_000)]
    public int TimeoutMs { get; init; } = 2_000;

    [Range(0, 10)]
    public int RetryCount { get; init; } = 2;

    [Range(0, 30_000)]
    public int RetryBaseDelayMs { get; init; } = 50;

    [Range(1, 100)]
    public int CircuitFailureThreshold { get; init; } = 3;

    [Range(1, 300_000)]
    public int CircuitBreakDurationMs { get; init; } = 5_000;
}

public sealed class DownstreamOptions
{
    public const string SectionName = "Downstream";

    [Required]
    [Url]
    public string BaseUrl { get; init; } = "http://localhost:5028/simulated/downstream";
}

public sealed class ReleaseOptions
{
    public const string SectionName = "Features";
    public bool NewPricingDarkLaunch { get; init; }
}

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    [Required]
    public string ServiceName { get; init; } = "contoso-storefront-api";

    [Required]
    public string Exporter { get; init; } = "Console";

    public string? OtlpEndpoint { get; init; }
    public string? ApplicationInsightsConnectionString { get; init; }
}

public static class ProductionDefaultsGuard
{
    public static IReadOnlyList<string> FindViolations(
        string environmentName,
        DatabaseOptions database,
        SecurityOptions security,
        CacheOptions cache,
        MessagingOptions messaging,
        KeyVaultOptions keyVault)
    {
        if (!string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var violations = new List<string>();

        if (security.SigningKey == SecurityOptions.DevelopmentSigningKey)
        {
            violations.Add("The development JWT signing key cannot be used in Production.");
        }

        if (string.IsNullOrWhiteSpace(security.Authority))
        {
            violations.Add("An external OIDC authority must be configured in Production.");
        }

        if (string.Equals(database.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("SQLite is a development default and cannot be used in Production.");
        }

        if (string.Equals(cache.Provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("The in-process memory cache cannot be used in Production.");
        }

        if (string.Equals(messaging.Provider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("The in-memory message bus cannot be used in Production.");
        }

        if (!keyVault.Enabled)
        {
            violations.Add("Key Vault integration must be enabled in Production.");
        }

        return violations;
    }
}
