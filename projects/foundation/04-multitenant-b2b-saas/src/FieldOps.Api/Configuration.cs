using System.ComponentModel.DataAnnotations;

namespace FieldOps.Api;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; init; } = "Sqlite";
    [Required] public string ConnectionString { get; init; } = "Data Source=fieldops.db";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; init; } = "fieldops-dev";
    [Required] public string Audience { get; init; } = "fieldops-api";
    [Required, MinLength(32)] public string SigningKey { get; init; } = "dev-only-not-a-real-secret-change-me-0123456789";
    public int TokenMinutes { get; init; } = 120;
}

public sealed class TenantResolutionOptions
{
    public const string SectionName = "TenantResolution";
    public string[] Precedence { get; init; } = ["jwt", "header", "subdomain"];
    public string HeaderName { get; init; } = "X-Tenant";
    public string BaseDomain { get; init; } = "fieldops.local";
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; init; } = ["http://localhost:3004", "http://localhost:5004"];
}
