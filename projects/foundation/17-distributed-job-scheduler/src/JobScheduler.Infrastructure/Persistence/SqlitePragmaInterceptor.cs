using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JobScheduler.Infrastructure.Persistence;

/// <summary>
/// Applies SQLite pragmas on every opened connection. <c>busy_timeout</c> makes concurrent writers
/// wait for the single-writer lock instead of failing with SQLITE_BUSY — essential because several
/// worker connections contend on the claim UPDATE. WAL improves read/write concurrency on file DBs.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await ApplyAsync(connection, cancellationToken);

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
