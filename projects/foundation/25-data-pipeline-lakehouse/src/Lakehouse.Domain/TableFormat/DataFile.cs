namespace Lakehouse.Domain.TableFormat;

/// <summary>The kind of change a snapshot represents. Mirrors the vocabulary of Delta/Iceberg commits.</summary>
public enum CommitOperation
{
    CreateTable,
    Append,
    Overwrite,
    Merge,
    Delete,
    EvolveSchema
}

/// <summary>
/// Metadata for one immutable data file in the lake. The physical bytes never change once written;
/// updates and deletes are expressed by adding new files and logically removing old ones in a later
/// snapshot (copy-on-write).
/// </summary>
public sealed record DataFile(
    string RelativePath,
    long RowCount,
    int SchemaVersion,
    string? Partition,
    long AddedBySnapshot);
