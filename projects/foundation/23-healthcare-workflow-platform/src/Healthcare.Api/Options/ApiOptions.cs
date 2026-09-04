using System.ComponentModel.DataAnnotations;

namespace Healthcare.Api.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; set; } = "healthcare-workflow-demo";
    [Required] public string Audience { get; set; } = "healthcare-workflow-demo-clients";
    [Required, MinLength(32)] public string SigningKey { get; set; } =
        "dev-only-not-a-real-secret-change-me-0123456789";
    public int LifetimeMinutes { get; set; } = 120;
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; set; } = "Sqlite";
    [Required] public string ConnectionString { get; set; } = "Data Source=healthcare.db";
}

public sealed class ReminderOptions
{
    public const string SectionName = "Reminders";
    public int[] LeadTimesMinutes { get; set; } = new[] { 2880, 120 };
    public int MaxAttempts { get; set; } = 3;
    public bool DispatchEnabled { get; set; } = true;
    public int DispatchPeriodSeconds { get; set; } = 60;
}
