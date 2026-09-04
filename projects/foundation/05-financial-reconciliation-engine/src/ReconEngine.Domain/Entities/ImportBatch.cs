using ReconEngine.Domain.Enums;

namespace ReconEngine.Domain.Entities;

/// <summary>
/// An immutable record of a single file import: which side it belongs to, the file identity,
/// its checksum, and how many rows were accepted vs rejected. Rejected rows are captured with
/// their line numbers so an operator can produce a rejected-rows report.
/// </summary>
public class ImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public RecordSource Source { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    /// <summary>SHA-256 of the raw uploaded bytes — lets us detect a re-upload of the same file.</summary>
    public string FileChecksum { get; set; } = string.Empty;

    public int TotalRows { get; set; }

    public int AcceptedRows { get; set; }

    public int RejectedRows { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<ReconRecord> Records { get; set; } = new List<ReconRecord>();

    public ICollection<ImportRejection> Rejections { get; set; } = new List<ImportRejection>();
}

/// <summary>A single row that failed validation during import, retained for the rejected-rows report.</summary>
public class ImportRejection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImportBatchId { get; set; }
    public int LineNumber { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RawLine { get; set; } = string.Empty;
}
