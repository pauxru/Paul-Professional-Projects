using System.ComponentModel.DataAnnotations;

namespace IntegrationHub.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; init; } = "Sqlite";

    [Required]
    public string ConnectionString { get; init; } = "Data Source=integration-hub.db";
}

public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    [Required]
    public string FilePath { get; init; } = "data/secrets.enc";

    [Required]
    public string MasterKey { get; init; } = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
}

public sealed class ConnectorHostOptions
{
    public const string SectionName = "Connectors";

    [Required]
    public string CrmBaseUrl { get; init; } = "http://localhost:5112";

    [Required]
    public string ErpBaseUrl { get; init; } = "http://localhost:5212";

    [Required]
    public string PaymentsBaseUrl { get; init; } = "http://localhost:5312";

    public string[] AllowedHosts { get; init; } = ["localhost"];

    public bool AllowPrivateNetworks { get; init; } = true;
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";
    [Range(1, 3650)]
    public int RunHistoryDays { get; init; } = 30;
}
