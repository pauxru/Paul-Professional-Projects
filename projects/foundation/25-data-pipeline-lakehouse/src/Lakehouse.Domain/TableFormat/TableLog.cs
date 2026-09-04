namespace Lakehouse.Domain.TableFormat;

/// <summary>
/// Pure transaction-log algebra. Given the ordered snapshot history of a table, resolve the set of live
/// data files as of a snapshot id or a point in time. No I/O lives here so the isolation/time-travel
/// rules can be unit tested exhaustively.
/// </summary>
public static class TableLog
{
    /// <summary>The id of the latest committed snapshot, or 0 when the table has no commits.</summary>
    public static long CurrentSnapshotId(IReadOnlyList<Snapshot> history)
        => history.Count == 0 ? 0 : history[^1].Id;

    /// <summary>
    /// Resolve the newest snapshot whose commit time is at or before <paramref name="asOf"/>. Returns null
    /// when the table did not yet exist at that time.
    /// </summary>
    public static Snapshot? SnapshotAsOf(IReadOnlyList<Snapshot> history, DateTimeOffset asOf)
    {
        Snapshot? found = null;
        foreach (var s in history.OrderBy(s => s.Id))
        {
            if (s.TimestampUtc <= asOf) found = s;
            else break;
        }
        return found;
    }

    /// <summary>
    /// Replay the log up to and including <paramref name="snapshotId"/> and return the live files. A
    /// snapshotId of 0 (or below the first commit) yields an empty set — the table-before-creation view.
    /// </summary>
    public static IReadOnlyList<DataFile> LiveFiles(IReadOnlyList<Snapshot> history, long snapshotId)
    {
        var live = new Dictionary<string, DataFile>(StringComparer.Ordinal);
        foreach (var s in history.OrderBy(s => s.Id))
        {
            if (s.Id > snapshotId) break;
            foreach (var removed in s.RemovedFilePaths) live.Remove(removed);
            foreach (var added in s.AddedFiles) live[added.RelativePath] = added;
        }
        return live.Values.ToList();
    }

    /// <summary>The schema version in effect at a given snapshot.</summary>
    public static int SchemaVersionAt(IReadOnlyList<Snapshot> history, long snapshotId)
    {
        var version = 1;
        foreach (var s in history.OrderBy(s => s.Id))
        {
            if (s.Id > snapshotId) break;
            version = s.SchemaVersion;
        }
        return version;
    }
}
