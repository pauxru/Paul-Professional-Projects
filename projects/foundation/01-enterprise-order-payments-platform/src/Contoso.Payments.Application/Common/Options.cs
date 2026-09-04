using System.ComponentModel.DataAnnotations;

namespace Contoso.Payments.Application.Common;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; set; } = "Sqlite";
    [Required] public string ConnectionString { get; set; } = "Data Source=contoso-payments.db";
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";
    [Required] public string Provider { get; set; } = "InMemory";
}

public sealed class CacheOptions
{
    public const string SectionName = "Cache";
    [Required] public string Provider { get; set; } = "Memory";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; set; } = "https://contoso-payments.local";
    [Required] public string Audience { get; set; } = "contoso-payments-api";
    [Required, MinLength(32)] public string SigningKey { get; set; } = string.Empty;
}

public sealed class PaymentProviderOptions
{
    public const string SectionName = "PaymentProvider";
    /// <summary>Deterministic | RandomFailures.  Only Deterministic is used in tests.</summary>
    public string Mode { get; set; } = "Deterministic";
    [Required, MinLength(32)] public string WebhookSigningSecret { get; set; } = string.Empty;
    public int WebhookTimestampToleranceSeconds { get; set; } = 300;

    /// <summary>Fault injection: percentage 0..100 of authorize calls to time out.</summary>
    public int TimeoutInjectionPercent { get; set; } = 0;
    /// <summary>Fault injection: percentage 0..100 of authorize calls to decline.</summary>
    public int DeclineInjectionPercent { get; set; } = 0;

    /// <summary>Base latency (ms) added to every simulated call.</summary>
    public int SimulatedLatencyMs { get; set; } = 5;

    /// <summary>When true, first authorize call for an intent returns <c>AsynchronousPending</c>.</summary>
    public bool AsynchronousCaptureMode { get; set; } = false;
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";
    public int PollIntervalMilliseconds { get; set; } = 250;
    public int MaxAttempts { get; set; } = 5;
    public int BaseBackoffMilliseconds { get; set; } = 100;
    public int BatchSize { get; set; } = 32;
}
