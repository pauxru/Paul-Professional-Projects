using System.ComponentModel.DataAnnotations;

namespace ZeroTrust.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; set; } = "Sqlite";
    [Required] public string ConnectionString { get; set; } = "Data Source=zero-trust.db";
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";
    [Range(1, 1_000_000)] public int PartnerPermitsPerMinute { get; set; } = 60;
    [Range(1, 1_000_000)] public int AdminPermitsPerMinute { get; set; } = 30;
    [Range(1, 1_000_000)] public int UserPermitsPerMinute { get; set; } = 120;
    [Range(1, 1_000_000)] public int PublicPermitsPerMinute { get; set; } = 30;
}
