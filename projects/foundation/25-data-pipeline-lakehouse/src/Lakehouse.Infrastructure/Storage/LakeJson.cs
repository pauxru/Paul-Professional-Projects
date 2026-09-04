using System.Text.Json;
using System.Text.Json.Serialization;
using Lakehouse.Domain.Schemas;
using Lakehouse.Domain.TableFormat;

namespace Lakehouse.Infrastructure.Storage;

/// <summary>JSON (de)serialization for the table format's metadata: schema files and snapshot log files.</summary>
internal static class LakeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    // ---- schema ----
    private sealed record SchemaDoc(int Version, List<ColumnDef> Columns, List<string> PrimaryKey);

    public static string SerializeSchema(TableSchema schema)
        => JsonSerializer.Serialize(new SchemaDoc(schema.Version, schema.Columns.ToList(), schema.PrimaryKey.ToList()), Options);

    public static TableSchema DeserializeSchema(string json)
    {
        var doc = JsonSerializer.Deserialize<SchemaDoc>(json, Options)
                  ?? throw new InvalidDataException("Corrupt schema file.");
        return new TableSchema(doc.Version, doc.Columns, doc.PrimaryKey);
    }

    // ---- snapshot ----
    private sealed record SnapshotDoc(
        long Id, long? ParentId, DateTimeOffset TimestampUtc, CommitOperation Operation, int SchemaVersion,
        List<DataFile> AddedFiles, List<string> RemovedFilePaths, Dictionary<string, string> Summary);

    public static string SerializeSnapshot(Snapshot snapshot)
        => JsonSerializer.Serialize(new SnapshotDoc(
            snapshot.Id, snapshot.ParentId, snapshot.TimestampUtc, snapshot.Operation, snapshot.SchemaVersion,
            snapshot.AddedFiles.ToList(), snapshot.RemovedFilePaths.ToList(),
            new Dictionary<string, string>(snapshot.Summary)), Options);

    public static Snapshot DeserializeSnapshot(string json)
    {
        var doc = JsonSerializer.Deserialize<SnapshotDoc>(json, Options)
                  ?? throw new InvalidDataException("Corrupt snapshot file.");
        return new Snapshot(doc.Id, doc.ParentId, doc.TimestampUtc, doc.Operation, doc.SchemaVersion,
            doc.AddedFiles, doc.RemovedFilePaths, doc.Summary);
    }
}

/// <summary>Raised when a commit cannot be published because a concurrent writer already claimed the id.</summary>
public sealed class TableConcurrencyException(string message) : Exception(message);
