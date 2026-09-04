using System.ComponentModel.DataAnnotations;

namespace Lab.Application.Contracts;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; init; } = "Sqlite";

    [Required]
    public string ConnectionString { get; init; } = "Data Source=northstar-lab.db";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public const string DefaultDevelopmentSigningKey = "dev-only-not-a-real-secret-change-me-0123456789";

    [Required]
    public string Issuer { get; init; } = "northstar-lab";

    [Required]
    public string Audience { get; init; } = "northstar-lab-api";

    [Required, MinLength(32)]
    public string SigningKey { get; init; } = DefaultDevelopmentSigningKey;

    [Range(60, 86_400)]
    public int ExpirySeconds { get; init; } = 3_600;
}
