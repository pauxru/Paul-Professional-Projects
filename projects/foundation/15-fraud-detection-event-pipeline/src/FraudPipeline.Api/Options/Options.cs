using System.ComponentModel.DataAnnotations;

namespace FraudPipeline.Api.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; set; } = "Sqlite";
    [Required] public string ConnectionString { get; set; } = "Data Source=fraudpipeline.db";
}

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";
    [Range(1, 10000)] public int RequestsPerMinute { get; set; } = 300;
    [Range(1, 10000)] public int BurstSize { get; set; } = 60;
}
