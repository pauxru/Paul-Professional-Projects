using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ExampleBank.Ledger.Infrastructure.Persistence;

/// <summary>
/// Applies SQLite pragmas on every opened connection: a busy timeout so concurrent writers wait
/// (and our retry policy rarely needs to fire) rather than failing immediately with SQLITE_BUSY,
/// and enforced foreign keys.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }
}
