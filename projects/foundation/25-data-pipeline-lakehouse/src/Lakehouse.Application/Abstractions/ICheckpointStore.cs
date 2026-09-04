namespace Lakehouse.Application.Abstractions;

/// <summary>
/// Durable key/value + watermark store used for exactly-once-effective ingestion and incremental
/// high-water marks. A crashed run re-reads its watermark and resumes without re-emitting rows.
/// </summary>
public interface ICheckpointStore
{
    long GetWatermark(string key);
    void SetWatermark(string key, long value);
    string? Get(string key);
    void Set(string key, string value);
}
