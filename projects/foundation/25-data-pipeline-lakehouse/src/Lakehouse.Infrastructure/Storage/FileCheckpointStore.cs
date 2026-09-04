using System.Collections.Concurrent;

namespace Lakehouse.Infrastructure.Storage;

/// <summary>File-backed checkpoint store: one small file per key under <c>&lt;root&gt;/_checkpoints</c>.
/// Writes are atomic (temp + rename) so a crash cannot leave a half-written watermark.</summary>
public sealed class FileCheckpointStore : Lakehouse.Application.Abstractions.ICheckpointStore
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, object> _locks = new();

    public FileCheckpointStore(string rootPath)
    {
        _dir = Path.Combine(rootPath, "_checkpoints");
        Directory.CreateDirectory(_dir);
    }

    public long GetWatermark(string key)
    {
        var raw = Get(key);
        return raw is not null && long.TryParse(raw, out var v) ? v : 0;
    }

    public void SetWatermark(string key, long value) => Set(key, value.ToString());

    public string? Get(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Set(string key, string value)
    {
        lock (_locks.GetOrAdd(key, _ => new object()))
        {
            var path = PathFor(key);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, value);
            File.Move(tmp, path, overwrite: true);
        }
    }

    private string PathFor(string key)
    {
        var safe = new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return Path.Combine(_dir, safe + ".txt");
    }
}
