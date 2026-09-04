using System.Collections.Concurrent;
using Lakehouse.Application.Abstractions;
using Lakehouse.Domain.Data;
using Lakehouse.Domain.Schemas;
using Lakehouse.Domain.TableFormat;

namespace Lakehouse.Infrastructure.Storage;

/// <summary>
/// A single table in the file-system lake, implementing a small Delta-Lake-like table format.
///
/// Physical layout:
/// <code>
///   &lt;root&gt;/&lt;table&gt;/
///     _schemas/000001.json ...      registered schema per version
///     _log/00000000000000000001.json ...  one immutable file per atomic commit (a Snapshot)
///     data/&lt;partition&gt;/part-*.jsonl      immutable data files (never deleted → time travel always works)
/// </code>
///
/// Guarantees: commits are atomic (data files are fully written before the log file is published via an
/// atomic rename); readers get snapshot isolation (a scan resolves one snapshot id and reads exactly its
/// live files); time travel by snapshot id or timestamp; additive schema evolution; MERGE upsert and
/// delete-by-predicate via copy-on-write. Not implemented (documented as such): file-level compaction,
/// VACUUM/retention, and cross-process optimistic retry.
/// </summary>
public sealed class LakeTable : ILakeTable
{
    private static readonly ConcurrentDictionary<string, object> Locks = new();

    private readonly string _dir;
    private readonly string _logDir;
    private readonly string _schemaDir;
    private readonly string _dataDir;
    private readonly IDataFileFormat _format;
    private readonly IClock _clock;

    public string Name { get; }

    public LakeTable(string rootPath, string name, IDataFileFormat format, IClock clock)
    {
        Name = name;
        _format = format;
        _clock = clock;
        _dir = Path.Combine(rootPath, name);
        _logDir = Path.Combine(_dir, "_log");
        _schemaDir = Path.Combine(_dir, "_schemas");
        _dataDir = Path.Combine(_dir, "data");
    }

    public bool Exists => Directory.Exists(_logDir) && Directory.EnumerateFiles(_logDir, "*.json").Any();

    public long CurrentSnapshotId => TableLog.CurrentSnapshotId(History());

    public TableSchema Schema => LoadSchema(LatestSchemaVersion());

    public IReadOnlyList<Snapshot> History()
    {
        if (!Directory.Exists(_logDir)) return Array.Empty<Snapshot>();
        return Directory.EnumerateFiles(_logDir, "*.json")
            .Where(p => !Path.GetFileName(p).StartsWith("tmp-", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => LakeJson.DeserializeSnapshot(File.ReadAllText(p)))
            .OrderBy(s => s.Id)
            .ToList();
    }

    // ---- writes ---------------------------------------------------------------------------------

    public Snapshot Create(TableSchema schema)
    {
        lock (LockFor())
        {
            if (Exists) throw new InvalidOperationException($"Table '{Name}' already exists.");
            Directory.CreateDirectory(_logDir);
            Directory.CreateDirectory(_schemaDir);
            Directory.CreateDirectory(_dataDir);
            WriteSchema(schema);
            return CommitLocked(CommitOperation.CreateTable, schema.Version, Array.Empty<Row>(), null,
                Array.Empty<string>(), new Dictionary<string, string> { ["rows"] = "0" });
        }
    }

    public Snapshot Append(IReadOnlyList<Row> rows, string? partition = null, IReadOnlyDictionary<string, string>? summary = null)
    {
        lock (LockFor())
        {
            EnsureExists();
            var meta = new Dictionary<string, string>(summary ?? new Dictionary<string, string>()) { ["added_rows"] = rows.Count.ToString() };
            return CommitLocked(CommitOperation.Append, LatestSchemaVersion(), rows, partition, Array.Empty<string>(), meta);
        }
    }

    public Snapshot Overwrite(IReadOnlyList<Row> rows, IReadOnlyDictionary<string, string>? summary = null)
    {
        lock (LockFor())
        {
            EnsureExists();
            var removed = TableLog.LiveFiles(History(), CurrentSnapshotId).Select(f => f.RelativePath).ToList();
            var meta = new Dictionary<string, string>(summary ?? new Dictionary<string, string>()) { ["rows"] = rows.Count.ToString() };
            return CommitLocked(CommitOperation.Overwrite, LatestSchemaVersion(), rows, null, removed, meta);
        }
    }

    public Snapshot Merge(IReadOnlyList<Row> rows, IReadOnlyList<string> keyColumns)
    {
        lock (LockFor())
        {
            EnsureExists();
            var current = Scan();
            var merged = new Dictionary<string, Row>(StringComparer.Ordinal);
            string Key(Row r) => string.Join("\u0001", keyColumns.Select(k => r[k]?.ToString() ?? "\u0000"));
            foreach (var r in current) merged[Key(r)] = r;
            var affected = 0;
            foreach (var r in rows) { merged[Key(r)] = r; affected++; }

            var removed = TableLog.LiveFiles(History(), CurrentSnapshotId).Select(f => f.RelativePath).ToList();
            var meta = new Dictionary<string, string> { ["merged_rows"] = affected.ToString(), ["result_rows"] = merged.Count.ToString() };
            return CommitLocked(CommitOperation.Merge, LatestSchemaVersion(), merged.Values.ToList(), null, removed, meta);
        }
    }

    public Snapshot Delete(Func<Row, bool> predicate)
    {
        lock (LockFor())
        {
            EnsureExists();
            var current = Scan();
            var kept = current.Where(r => !predicate(r)).ToList();
            var deleted = current.Count - kept.Count;
            if (deleted == 0) return History()[^1];

            var removed = TableLog.LiveFiles(History(), CurrentSnapshotId).Select(f => f.RelativePath).ToList();
            var meta = new Dictionary<string, string> { ["deleted_rows"] = deleted.ToString(), ["result_rows"] = kept.Count.ToString() };
            return CommitLocked(CommitOperation.Delete, LatestSchemaVersion(), kept, null, removed, meta);
        }
    }

    public Snapshot EvolveSchema(TableSchema newSchema)
    {
        lock (LockFor())
        {
            EnsureExists();
            var current = LoadSchema(LatestSchemaVersion());
            if (newSchema.Version != current.Version + 1)
                throw new InvalidOperationException($"Schema evolution must bump version to {current.Version + 1}.");
            WriteSchema(newSchema);
            return CommitLocked(CommitOperation.EvolveSchema, newSchema.Version, Array.Empty<Row>(), null,
                Array.Empty<string>(), new Dictionary<string, string> { ["schema_version"] = newSchema.Version.ToString() });
        }
    }

    // ---- reads ----------------------------------------------------------------------------------

    public IReadOnlyList<Row> Scan(long? snapshotId = null)
    {
        var history = History();
        if (history.Count == 0) return Array.Empty<Row>();
        var sid = snapshotId ?? TableLog.CurrentSnapshotId(history);
        var schema = LoadSchema(TableLog.SchemaVersionAt(history, sid));
        var files = TableLog.LiveFiles(history, sid).OrderBy(f => f.RelativePath, StringComparer.Ordinal);

        var rows = new List<Row>();
        foreach (var file in files)
        {
            var abs = Path.Combine(_dir, file.RelativePath);
            if (File.Exists(abs))
                rows.AddRange(_format.Read(abs, schema));
        }
        return rows;
    }

    public IReadOnlyList<Row> ScanAsOf(DateTimeOffset asOf)
    {
        var history = History();
        var snap = TableLog.SnapshotAsOf(history, asOf);
        return snap is null ? Array.Empty<Row>() : Scan(snap.Id);
    }

    // ---- internals ------------------------------------------------------------------------------

    private object LockFor() => Locks.GetOrAdd(_dir, _ => new object());

    private void EnsureExists()
    {
        if (!Exists) throw new InvalidOperationException($"Table '{Name}' does not exist; call Create first.");
    }

    private Snapshot CommitLocked(CommitOperation op, int schemaVersion, IReadOnlyList<Row> rows, string? partition,
        IReadOnlyList<string> removedPaths, Dictionary<string, string> summary)
    {
        var history = History();
        var currentId = TableLog.CurrentSnapshotId(history);
        var nextId = currentId + 1;
        var schema = LoadSchema(schemaVersion);

        var added = new List<DataFile>();
        if (rows.Count > 0)
        {
            var partitionDir = partition is null ? _dataDir : Path.Combine(_dataDir, partition);
            Directory.CreateDirectory(partitionDir);
            var fileName = $"part-{nextId:D8}-{Guid.NewGuid():N}{_format.Extension}";
            var abs = Path.Combine(partitionDir, fileName);
            _format.Write(abs, rows, schema);
            var rel = Path.GetRelativePath(_dir, abs).Replace('\\', '/');
            added.Add(new DataFile(rel, rows.Count, schemaVersion, partition, nextId));
        }

        var snapshot = new Snapshot(nextId, currentId == 0 ? null : currentId, _clock.UtcNow, op, schemaVersion,
            added, removedPaths, summary);

        PublishAtomically(snapshot);
        return snapshot;
    }

    private void PublishAtomically(Snapshot snapshot)
    {
        Directory.CreateDirectory(_logDir);
        var tmp = Path.Combine(_logDir, $"tmp-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, LakeJson.SerializeSnapshot(snapshot));
        var final = Path.Combine(_logDir, $"{snapshot.Id:D20}.json");
        try
        {
            // Atomic publish: the log file appears in one step, and only after the data files are flushed.
            File.Move(tmp, final, overwrite: false);
        }
        catch (IOException)
        {
            File.Delete(tmp);
            throw new TableConcurrencyException(
                $"Snapshot {snapshot.Id} for '{Name}' already exists — a concurrent writer won the commit race.");
        }
    }

    private int LatestSchemaVersion()
    {
        if (!Directory.Exists(_schemaDir)) return 1;
        var versions = Directory.EnumerateFiles(_schemaDir, "*.json")
            .Select(p => int.TryParse(Path.GetFileNameWithoutExtension(p), out var v) ? v : 0)
            .DefaultIfEmpty(1);
        return versions.Max();
    }

    private TableSchema LoadSchema(int version)
    {
        var path = Path.Combine(_schemaDir, $"{version:D6}.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Schema v{version} for table '{Name}' not found.", path);
        return LakeJson.DeserializeSchema(File.ReadAllText(path));
    }

    private void WriteSchema(TableSchema schema)
    {
        Directory.CreateDirectory(_schemaDir);
        File.WriteAllText(Path.Combine(_schemaDir, $"{schema.Version:D6}.json"), LakeJson.SerializeSchema(schema));
    }
}
