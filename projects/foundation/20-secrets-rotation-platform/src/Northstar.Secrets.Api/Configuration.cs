using System.ComponentModel.DataAnnotations;

namespace Northstar.Secrets.Api;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; set; } = "Sqlite";

    [Required]
    public string ConnectionString { get; set; } = "Data Source=secrets.db";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string DevelopmentDefault =
        "demo-only-not-a-real-secret-jwt-signing-key-change-me-0123456789";

    [Required]
    public string Issuer { get; set; } = "northstar-secrets-local";

    [Required]
    public string Audience { get; set; } = "northstar-secrets-api";

    [Required, MinLength(32)]
    public string SigningKey { get; set; } = DevelopmentDefault;
}

public sealed class CryptoOptions
{
    public const string SectionName = "Crypto";

    [Required]
    public string KeyVersion { get; set; } = "local-v1";

    [Required]
    public string MasterKey { get; set; } =
        "demo-only-not-a-real-secret-master-key-v1";
}

public sealed class RotationConfiguration
{
    public const string SectionName = "Rotation";
    [Range(1, 1440)]
    public int AcknowledgementTimeoutMinutes { get; set; } = 15;
    [Range(5, 3600)]
    public int SchedulerIntervalSeconds { get; set; } = 60;
}

public sealed class NotificationConfiguration
{
    public const string SectionName = "Notifications";

    [Required]
    public string WebhookSigningKey { get; set; } =
        "demo-only-not-a-real-secret-webhook-signing-key";

    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;
}
