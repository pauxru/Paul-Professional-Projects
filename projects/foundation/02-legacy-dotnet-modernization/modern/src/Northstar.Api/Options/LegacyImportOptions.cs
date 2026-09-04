namespace Northstar.Api.Options;

public sealed class LegacyImportOptions
{
    public const string SectionName = "LegacyImport";

    public string ConnectionString { get; init; } = "Data Source=../../../legacy/Northstar.Legacy.Web/legacy-northstar.db";
}
