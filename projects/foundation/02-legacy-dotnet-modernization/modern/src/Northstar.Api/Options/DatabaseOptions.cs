using System.ComponentModel.DataAnnotations;

namespace Northstar.Api.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    [RegularExpression("Sqlite|Npgsql", ErrorMessage = "Provider must be Sqlite or Npgsql.")]
    public string Provider { get; init; } = "Sqlite";

    [Required]
    public string ConnectionString { get; init; } = "Data Source=northstar-modern.db";
}
