using Lakehouse.Domain.Data;

namespace Lakehouse.Application.Model;

/// <summary>
/// Reserved metadata columns stamped onto every bronze row. Prefixed with '_' so they never collide
/// with business columns. These carry full source provenance (file, offset, ingest time, run id) plus
/// the CDC operation and ordering keys — the raw material for lineage and exactly-once ingestion.
/// </summary>
public static class Meta
{
    public const string Op = "_op";                 // I/U/D
    public const string Sequence = "_sequence";     // source log position
    public const string CommitTs = "_commit_ts";    // source commit time
    public const string IngestTs = "_ingest_ts";    // when bronze ingested it
    public const string IngestDate = "_ingest_date"; // partition key (yyyy-MM-dd)
    public const string Source = "_source";         // source file name
    public const string SourceOffset = "_source_offset"; // offset within source file
    public const string RunId = "_run_id";          // ingestion run id

    public static readonly IReadOnlyList<string> All = new[]
    {
        Op, Sequence, CommitTs, IngestTs, IngestDate, Source, SourceOffset, RunId
    };

    /// <summary>Attach ingestion metadata to a raw business row, returning a new row.</summary>
    public static Row Stamp(Row business, string op, long sequence, DateTimeOffset commitTs,
        DateTimeOffset ingestTs, string source, long sourceOffset, string runId)
    {
        var row = business.Clone();
        row[Op] = op;
        row[Sequence] = sequence;
        row[CommitTs] = commitTs;
        row[IngestTs] = ingestTs;
        row[IngestDate] = ingestTs.UtcDateTime.ToString("yyyy-MM-dd");
        row[Source] = source;
        row[SourceOffset] = sourceOffset;
        row[RunId] = runId;
        return row;
    }
}
