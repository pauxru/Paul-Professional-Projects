using System.Collections.Concurrent;
using Collab.Application.Abstractions;

namespace Collab.Infrastructure.Realtime;

/// <summary>
/// Process-wide cache of live <see cref="DocumentRuntime"/> instances. Loading is serialized per
/// document with a keyed semaphore so two connections joining the same document at once rebuild it
/// only once. The runtimes themselves are the in-memory authoritative editing state; persistence is
/// the durable source of truth they are rebuilt from after an eviction or restart.
/// </summary>
public sealed class DocumentRuntimeCache : IDocumentRuntimeCache
{
    private readonly ConcurrentDictionary<Guid, DocumentRuntime> _runtimes = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _loadLocks = new();

    public async Task<DocumentRuntime> GetOrLoadAsync(
        Guid documentId, Func<CancellationToken, Task<DocumentRuntime>> loader, CancellationToken ct)
    {
        if (_runtimes.TryGetValue(documentId, out var existing))
            return existing;

        var gate = _loadLocks.GetOrAdd(documentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_runtimes.TryGetValue(documentId, out existing))
                return existing;

            var runtime = await loader(ct);
            _runtimes[documentId] = runtime;
            return runtime;
        }
        finally
        {
            gate.Release();
        }
    }

    public DocumentRuntime? TryGet(Guid documentId) =>
        _runtimes.TryGetValue(documentId, out var runtime) ? runtime : null;

    public void Evict(Guid documentId) => _runtimes.TryRemove(documentId, out _);
}
