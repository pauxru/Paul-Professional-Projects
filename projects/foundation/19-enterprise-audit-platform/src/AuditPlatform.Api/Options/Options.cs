using System.ComponentModel.DataAnnotations;

namespace AuditPlatform.Api.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; set; } = "Sqlite";
    [Required, MinLength(3)] public string ConnectionString { get; set; } = "Data Source=audit.db";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; set; } = "audit-platform";
    [Required] public string Audience { get; set; } = "audit-platform-clients";
    [Required, MinLength(32)] public string SigningKey { get; set; } = "dev-only-not-a-real-secret-change-me-0123456789";
}

public sealed class SigningOptions
{
    public const string SectionName = "Signing";
    [Required] public string KeyId { get; set; } = "audit-dev-key-1";
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";
    public bool Enabled { get; set; } = true;
    public string DefaultTenantId { get; set; } = "example-bank";
}
