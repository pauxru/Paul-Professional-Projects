using Microsoft.Data.Sqlite;
using Northstar.Application.Importing;

namespace Northstar.Infrastructure.Importing;

/// <summary>Anti-corruption adapter translating the legacy schema into neutral import rows.</summary>
public sealed class LegacySqliteClaimSource(string connectionString) : ILegacyClaimSource
{
    public async Task<IReadOnlyList<LegacyClaimRow>> ReadClaimsAsync(CancellationToken cancellationToken)
    {
        var rows = new List<LegacyClaimRow>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Id, c.ClaimReference, c.PolicyId, p.PolicyNumber, h.Name, h.Email,
                   p.Deductible, p.PolicyLimit, c.ClaimedAmount, c.ReserveAmount, c.Currency,
                   c.Status, c.Adjuster, c.CreatedUtc
            FROM Claims c
            LEFT JOIN Policies p ON p.Id = c.PolicyId
            LEFT JOIN Policyholders h ON h.Id = p.PolicyholderId
            ORDER BY c.Id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyClaimRow(
                reader.GetInt64(0),
                ReadString(reader, 1),
                reader.GetInt64(2),
                ReadString(reader, 3),
                ReadString(reader, 4),
                ReadString(reader, 5),
                ReadDecimal(reader, 6),
                ReadDecimal(reader, 7),
                ReadDecimal(reader, 8),
                ReadDecimal(reader, 9),
                ReadString(reader, 10),
                ReadString(reader, 11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                DateTimeOffset.TryParse(ReadString(reader, 13), out var createdAt) ? createdAt : DateTimeOffset.UnixEpoch));
        }

        return rows;
    }

    private static string ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty;

    private static decimal ReadDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal));
}
