using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Orchestration;
using Lakehouse.Application.Quality;
using Lakehouse.Infrastructure.Pipeline;
using Lakehouse.Infrastructure.Quality;
using Lakehouse.Infrastructure.Sources;
using Lakehouse.Infrastructure.Storage;

namespace Lakehouse.UnitTests.Support;

/// <summary>
/// A disposable, isolated lakehouse rooted in a unique temp directory. Wires the real filesystem
/// implementations (table format, checkpoint/quality/run stores) with a <see cref="FakeClock"/> so tests
/// exercise production code paths without any shared or external state. Cleaned up on Dispose.
/// </summary>
public sealed class TempLake : IDisposable
{
    public string Root { get; }
    public FakeClock Clock { get; }
    public JsonlDataFileFormat Format { get; }
    public FileSystemLakehouse Lake { get; }
    public FileCheckpointStore Checkpoints { get; }
    public FileDataQualityStore Quality { get; }
    public FileRunHistoryStore Runs { get; }

    public TempLake(FakeClock? clock = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "lh-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Clock = clock ?? new FakeClock();
        Format = new JsonlDataFileFormat();
        Lake = new FileSystemLakehouse(Root, Format, Clock);
        Checkpoints = new FileCheckpointStore(Root);
        Quality = new FileDataQualityStore(Root);
        Runs = new FileRunHistoryStore(Root);
    }

    public DataQualityRunner DqRunner() => new(Lake, Clock, Quality);

    /// <summary>A full pipeline wired to a specific in-memory source feed.</summary>
    public LakehousePipeline Pipeline(ISourceFeedProvider feed)
        => new(Lake, Checkpoints, Clock, DqRunner(), feed);

    public DagRunner Runner() => new(Runs, Clock);

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}

/// <summary>An <see cref="ISourceFeedProvider"/> over a fixed in-memory list — for deterministic tests.</summary>
public sealed class StaticFeedProvider(IReadOnlyList<Lakehouse.Domain.Cdc.ChangeEvent> events) : ISourceFeedProvider
{
    public IReadOnlyList<Lakehouse.Domain.Cdc.ChangeEvent> Load() => events;
}
