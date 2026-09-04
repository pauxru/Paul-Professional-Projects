using Lakehouse.Domain.Data;
using Lakehouse.Domain.Schemas;

namespace Lakehouse.Infrastructure.Storage;

/// <summary>
/// Pluggable physical file encoding for the lake. The table format (commits, snapshots, MERGE) is
/// deliberately independent of this so the encoding can be swapped without touching the log semantics.
/// The default is JSONL; a Parquet adapter would implement the same contract.
/// </summary>
public interface IDataFileFormat
{
    string Extension { get; }
    void Write(string absolutePath, IReadOnlyList<Row> rows, TableSchema schema);

    /// <summary>Read a file, coercing each value to the target schema's type. Columns absent in the file
    /// (older schema) surface as null, which is how schema evolution reads old and new files together.</summary>
    IReadOnlyList<Row> Read(string absolutePath, TableSchema targetSchema);
}
