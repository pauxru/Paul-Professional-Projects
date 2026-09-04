using Lakehouse.Domain.Data;
using Lakehouse.Domain.Schemas;
using Lakehouse.Domain.TableFormat;

namespace Lakehouse.Application.Abstractions;

/// <summary>The lake: a namespace of tables backed by the custom table format.</summary>
public interface ILakehouse
{
    string RootPath { get; }
    ILakeTable Table(string name);
    bool TableExists(string name);
    IReadOnlyList<string> ListTables();
}

/// <summary>
/// A single table in the lake exposing the custom Delta-like table format: atomic commits, snapshot
/// isolation, time travel, schema evolution, MERGE upsert and delete-by-predicate. All operations are
/// synchronous — this is a batch engine, not a request path — which keeps the commit semantics obvious.
/// </summary>
public interface ILakeTable
{
    string Name { get; }
    bool Exists { get; }

    /// <summary>The latest registered schema.</summary>
    TableSchema Schema { get; }

    /// <summary>Id of the newest committed snapshot (0 when empty).</summary>
    long CurrentSnapshotId { get; }

    /// <summary>The full commit history, oldest first.</summary>
    IReadOnlyList<Snapshot> History();

    Snapshot Create(TableSchema schema);

    /// <summary>Append immutable rows. Never mutates or removes existing files.</summary>
    Snapshot Append(IReadOnlyList<Row> rows, string? partition = null, IReadOnlyDictionary<string, string>? summary = null);

    /// <summary>Replace all live rows with the supplied set (copy-on-write full overwrite).</summary>
    Snapshot Overwrite(IReadOnlyList<Row> rows, IReadOnlyDictionary<string, string>? summary = null);

    /// <summary>Upsert by business key: matched rows are replaced, unmatched rows inserted.</summary>
    Snapshot Merge(IReadOnlyList<Row> rows, IReadOnlyList<string> keyColumns);

    /// <summary>Delete every live row matching the predicate (copy-on-write).</summary>
    Snapshot Delete(Func<Row, bool> predicate);

    /// <summary>Register a new, additively-evolved schema version.</summary>
    Snapshot EvolveSchema(TableSchema newSchema);

    /// <summary>Read the live rows as of a snapshot (default: latest) — snapshot-isolated.</summary>
    IReadOnlyList<Row> Scan(long? snapshotId = null);

    /// <summary>Time travel: read the live rows as of a wall-clock instant.</summary>
    IReadOnlyList<Row> ScanAsOf(DateTimeOffset asOf);
}
