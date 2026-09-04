using System.Text.Json;
using Lakehouse.Application.Orchestration;
using Lakehouse.Infrastructure.Serialization;

namespace Lakehouse.Infrastructure.Pipeline;

/// <summary>File-backed run-history store: one JSON file per run under <c>&lt;root&gt;/_runs</c>.</summary>
public sealed class FileRunHistoryStore : IRunHistoryStore
{
    private readonly string _dir;

    public FileRunHistoryStore(string rootPath)
    {
        _dir = Path.Combine(rootPath, "_runs");
        Directory.CreateDirectory(_dir);
    }

    public void Save(RunRecord record)
    {
        var name = $"{record.StartedAt.UtcDateTime:yyyyMMddHHmmssfff}-{Safe(record.Window)}-{Safe(record.RunId)}.json";
        var tmp = Path.Combine(_dir, name + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json.Options));
        File.Move(tmp, Path.Combine(_dir, name), overwrite: true);
    }

    public IReadOnlyList<RunRecord> Recent(int limit = 50) =>
        Load().OrderByDescending(r => r.StartedAt).Take(limit).ToList();

    public RunRecord? Latest() => Load().OrderByDescending(r => r.StartedAt).FirstOrDefault();

    private IEnumerable<RunRecord> Load()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            RunRecord? record = null;
            try { record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), Json.Options); }
            catch (JsonException) { /* skip corrupt record */ }
            if (record is not null) yield return record;
        }
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
