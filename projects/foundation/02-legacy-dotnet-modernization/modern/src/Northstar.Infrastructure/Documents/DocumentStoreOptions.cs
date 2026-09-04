using System.ComponentModel.DataAnnotations;

namespace Northstar.Infrastructure.Documents;

public sealed class DocumentStoreOptions
{
    public const string SectionName = "DocumentStore";

    [Required]
    public string Provider { get; init; } = "LocalFileSystem";

    [Required]
    public string RootPath { get; init; } = "App_Data/documents";

    [Range(1, 10_485_760)]
    public int MaxBytes { get; init; } = 5_242_880;
}
