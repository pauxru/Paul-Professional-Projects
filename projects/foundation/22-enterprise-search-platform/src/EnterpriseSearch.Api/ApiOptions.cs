using System.ComponentModel.DataAnnotations;

namespace EnterpriseSearch.Api;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; init; } = "Sqlite";
    [Required] public string ConnectionString { get; init; } = "Data Source=enterprise-search.db";
}

public sealed class TelemetryOptions
{
    public const string SectionName = "OpenTelemetry";
    public bool ConsoleExporter { get; init; }
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; init; } = [];
}
