using System.Diagnostics;
using System.Globalization;
using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Model;
using Lakehouse.Application.Serving;
using Lakehouse.Domain.Data;
using Lakehouse.Domain.Schemas;
using Microsoft.Data.Sqlite;

namespace Lakehouse.Infrastructure.Serving;

/// <summary>
/// SQLite-backed serving engine. Gold tables are projected into a SQLite database; queries run over a
/// dedicated <b>read-only</b> connection with a row cap and statement timeout. Only the gold serving
/// tables are loaded, so the query surface is a clean semantic boundary — bronze/silver and their PII
/// are simply not present. Combined with <see cref="SqlGuard"/> this rejects writes, DDL and injection.
/// </summary>
public sealed class SqliteQueryEngine : ISqlQueryEngine
{
    private readonly ILakehouse _lake;
    private readonly string _dbPath;
    private readonly string _readWrite;
    private readonly string _readOnly;
    private readonly object _rebuildLock = new();

    public SqliteQueryEngine(ILakehouse lake, string dbPath)
    {
        _lake = lake;
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _readWrite = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        _readOnly = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
    }

    public void Rebuild()
    {
        lock (_rebuildLock)
        {
            using var conn = new SqliteConnection(_readWrite);
            conn.Open();
            foreach (var name in Lakehouse.Application.Model.Tables.GoldServingTables)
            {
                var table = _lake.Table(name);
                if (!table.Exists) continue;
                var schema = table.Schema;
                var rows = table.Scan();
                LoadTable(conn, name, schema, rows);
            }
        }
    }

    private static void LoadTable(SqliteConnection conn, string name, TableSchema schema, IReadOnlyList<Row> rows)
    {
        var cols = schema.Columns;

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = $"DROP TABLE IF EXISTS \"{name}\";";
            drop.ExecuteNonQuery();
        }

        var ddl = string.Join(", ", cols.Select(c => $"\"{c.Name}\" {c.Type.ToSqliteType()}"));
        using (var create = conn.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE \"{name}\" ({ddl});";
            create.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        var parameters = cols.Select((c, i) => { var p = insert.CreateParameter(); p.ParameterName = $"@p{i}"; insert.Parameters.Add(p); return p; }).ToArray();
        insert.CommandText =
            $"INSERT INTO \"{name}\" ({string.Join(", ", cols.Select(c => $"\"{c.Name}\""))}) " +
            $"VALUES ({string.Join(", ", parameters.Select(p => p.ParameterName))});";

        foreach (var row in rows)
        {
            for (var i = 0; i < cols.Count; i++)
                parameters[i].Value = Coerce(row, cols[i]) ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Coerce a lake value to the CLR type SQLite should store for the column's affinity.</summary>
    private static object? Coerce(Row row, ColumnDef col) => col.Type switch
    {
        ColumnType.Long => row.GetLong(col.Name),
        ColumnType.Double => row.GetDouble(col.Name),
        ColumnType.Bool => row.GetBool(col.Name) is { } b ? (b ? 1L : 0L) : null,
        ColumnType.Decimal => row.GetDecimal(col.Name)?.ToString(CultureInfo.InvariantCulture),
        ColumnType.Timestamp => row.GetTimestamp(col.Name)?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        _ => row.GetString(col.Name)
    };

    public QueryResult Query(string sql, int maxRows = 1000, TimeSpan? timeout = null)
    {
        var safe = SqlGuard.Validate(sql);
        if (!File.Exists(_dbPath))
            throw new InvalidOperationException("Serving store is empty; run the pipeline first.");

        using var conn = new SqliteConnection(_readOnly);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = safe;
        cmd.CommandTimeout = (int)Math.Ceiling((timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds);

        var sw = Stopwatch.StartNew();
        using var reader = cmd.ExecuteReader();

        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        while (reader.Read())
        {
            if (rows.Count >= maxRows) { truncated = true; break; }
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }
        sw.Stop();
        return new QueryResult(columns, rows, truncated, sw.Elapsed.TotalMilliseconds);
    }

    public IReadOnlyList<string> Tables()
    {
        if (!File.Exists(_dbPath)) return Array.Empty<string>();
        using var conn = new SqliteConnection(_readOnly);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
