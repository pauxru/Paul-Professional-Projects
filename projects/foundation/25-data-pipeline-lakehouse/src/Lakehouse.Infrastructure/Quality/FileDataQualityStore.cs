using System.Text.Json;
using Lakehouse.Application.Quality;
using Lakehouse.Domain.Quality;
using Lakehouse.Infrastructure.Serialization;

namespace Lakehouse.Infrastructure.Quality;

/// <summary>File-backed data-quality report store: one JSON file per report under <c>&lt;root&gt;/_quality</c>.</summary>
public sealed class FileDataQualityStore : IDataQualityStore
{
    private readonly string _dir;

    public FileDataQualityStore(string rootPath)
    {
        _dir = Path.Combine(rootPath, "_quality");
        Directory.CreateDirectory(_dir);
    }

    public void Save(DataQualityReport report)
    {
        var name = $"{Safe(report.Dataset)}-{report.EvaluatedAt.UtcDateTime:yyyyMMddHHmmssfff}-{Safe(report.RunId)}.json";
        var tmp = Path.Combine(_dir, name + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(report, Json.Options));
        File.Move(tmp, Path.Combine(_dir, name), overwrite: true);
    }

    public DataQualityReport? Latest(string gate) =>
        Load().Where(r => string.Equals(r.Dataset, gate, StringComparison.Ordinal))
              .OrderByDescending(r => r.EvaluatedAt).FirstOrDefault();

    public IReadOnlyList<DataQualityReport> Recent(int limit = 50) =>
        Load().OrderByDescending(r => r.EvaluatedAt).Take(limit).ToList();

    private IEnumerable<DataQualityReport> Load()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            DataQualityReport? report = null;
            try { report = JsonSerializer.Deserialize<DataQualityReport>(File.ReadAllText(file), Json.Options); }
            catch (JsonException) { /* skip corrupt report */ }
            if (report is not null) yield return report;
        }
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
