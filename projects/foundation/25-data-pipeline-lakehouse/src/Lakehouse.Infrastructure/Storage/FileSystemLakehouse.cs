using System.Collections.Concurrent;
using Lakehouse.Application.Abstractions;

namespace Lakehouse.Infrastructure.Storage;

/// <summary>
/// A lake rooted at a filesystem directory. Each child directory containing a <c>_log</c> folder is a
/// table. Handles are cached per name; the table format itself provides the isolation guarantees.
/// </summary>
public sealed class FileSystemLakehouse : ILakehouse
{
    private readonly IDataFileFormat _format;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, LakeTable> _tables = new();

    public string RootPath { get; }

    public FileSystemLakehouse(string rootPath, IDataFileFormat format, IClock clock)
    {
        RootPath = rootPath;
        _format = format;
        _clock = clock;
        Directory.CreateDirectory(rootPath);
    }

    public ILakeTable Table(string name) =>
        _tables.GetOrAdd(name, n => new LakeTable(RootPath, n, _format, _clock));

    public bool TableExists(string name) => Table(name).Exists;

    public IReadOnlyList<string> ListTables()
    {
        if (!Directory.Exists(RootPath)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(RootPath)
            .Where(d => Directory.Exists(Path.Combine(d, "_log")))
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
