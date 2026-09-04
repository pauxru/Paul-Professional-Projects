namespace Lakehouse.Domain.TableFormat;

/// <summary>
/// One atomic commit in a table's transaction log. A snapshot records which files were added and which
/// were logically removed, the schema version in effect, and a summary (row counts etc.). The set of
/// live files "as of" a snapshot is the replay of adds/removes from the first snapshot up to it, which
/// is what gives readers snapshot isolation and time travel.
/// </summary>
public sealed record Snapshot(
    long Id,
    long? ParentId,
    DateTimeOffset TimestampUtc,
    CommitOperation Operation,
    int SchemaVersion,
    IReadOnlyList<DataFile> AddedFiles,
    IReadOnlyList<string> RemovedFilePaths,
    IReadOnlyDictionary<string, string> Summary)
{
    public long AddedRecords => AddedFiles.Sum(f => f.RowCount);
}
