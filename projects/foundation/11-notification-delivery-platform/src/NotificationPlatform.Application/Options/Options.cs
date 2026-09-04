namespace NotificationPlatform.Application.Options;

using System.ComponentModel.DataAnnotations;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    [Range(1, 1000)]
    public int BulkMaxItems { get; set; } = 500;

    [Range(1, 60 * 24)]
    public int DedupWindowMinutes { get; set; } = 60;

    [Range(1, 20)]
    public int DefaultMaxAttempts { get; set; } = 5;

    [Range(50, 60_000)]
    public int BackoffBaseMilliseconds { get; set; } = 200;

    [Range(1000, 600_000)]
    public int BackoffMaxMilliseconds { get; set; } = 30_000;

    [Range(1, 10_000)]
    public int WorkerBatchSize { get; set; } = 25;

    [Range(1, 10_000)]
    public int PollIntervalMilliseconds { get; set; } = 200;

    [Range(1, 20)]
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    [Range(1, 20)]
    public int CircuitBreakerHalfOpenSuccess { get; set; } = 2;

    [Range(1, 3600)]
    public int CircuitBreakerOpenSeconds { get; set; } = 30;

    [Range(1, 100_000)]
    public int PerProviderRatePerSecond { get; set; } = 100;

    [Range(1, 100)]
    public int FrequencyCapPerDay { get; set; } = 3;

    [Range(1, 3600)]
    public int UnsubscribeTokenLifetimeDays { get; set; } = 30;

    [Range(1, 3600)]
    public int WebhookSignatureToleranceSeconds { get; set; } = 300;

    public bool DeterministicMode { get; set; } = false;

    public int Seed { get; set; } = 20260903;
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "notification-platform";

    [Required]
    public string Audience { get; set; } = "notification-platform-clients";

    [Required, MinLength(32)]
    public string SigningKey { get; set; } = "dev-only-not-a-real-secret-change-me-0123456789";
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; set; } = "Sqlite";

    [Required]
    public string ConnectionString { get; set; } = "Data Source=notifications.db";
}

public sealed class WebhookOptions
{
    public const string SectionName = "Webhook";

    [Required, MinLength(32)]
    public string SigningKey { get; set; } = "dev-only-webhook-signing-secret-please-change-0123456789";

    public string SignatureHeader { get; set; } = "X-Signature";
    public string TimestampHeader { get; set; } = "X-Timestamp";
    public string NonceHeader { get; set; } = "X-Nonce";
}
