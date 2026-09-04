using System.Collections.Concurrent;
using System.Threading.Channels;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public sealed class IndexingBackpressureException : Exception
{
    public IndexingBackpressureException(string message) : base(message) { }
}

public abstract record IndexOperation;
public sealed record UpsertIndexOperation(SearchDocument Document) : IndexOperation;
public sealed record PatchIndexOperation(string DocumentId, DocumentPatch Patch) : IndexOperation;
public sealed record DeleteIndexOperation(string DocumentId) : IndexOperation;

public sealed class SearchIndexHandle
{
    private readonly Channel<IndexOperation> _queue;
    private long _queued;

    public SearchIndexHandle(InvertedIndex index, int queueCapacity = 10_000)
    {
        Index = index;
        _queue = Channel.CreateBounded<IndexOperation>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public InvertedIndex Index { get; }
    public long QueuedCount => Interlocked.Read(ref _queued);

    public void Enqueue(IndexOperation operation)
    {
        if (!_queue.Writer.TryWrite(operation)) throw new IndexingBackpressureException("The indexing queue is full; retry after refresh.");
        Interlocked.Increment(ref _queued);
    }

    public int Refresh(DateTimeOffset timestamp)
    {
        var changed = 0;
        while (_queue.Reader.TryRead(out var operation))
        {
            Interlocked.Decrement(ref _queued);
            switch (operation)
            {
                case UpsertIndexOperation upsert:
                    Index.Upsert(upsert.Document);
                    changed++;
                    break;
                case PatchIndexOperation patch when Index.TryGetDocument(patch.DocumentId, out var document) && document is not null:
                    Index.Upsert(document.ApplyPatch(patch.Patch));
                    changed++;
                    break;
                case DeleteIndexOperation delete when Index.Delete(delete.DocumentId):
                    changed++;
                    break;
            }
        }
        if (changed > 0)
        {
            Index.MarkRefreshed(timestamp);
            SearchTelemetry.IndexedDocuments.Add(changed);
            SearchTelemetry.SetIndexSize(Index.GetStats().Documents);
        }
        return changed;
    }
}

public sealed class SearchCluster
{
    private readonly ReaderWriterLockSlim _gate = new();
    private readonly ConcurrentDictionary<string, SearchIndexHandle> _indices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITextAnalyzer _analyzer;
    private readonly IEmbeddingModel _embeddingModel;

    public SearchCluster(ITextAnalyzer analyzer, IEmbeddingModel embeddingModel)
    {
        _analyzer = analyzer;
        _embeddingModel = embeddingModel;
    }

    public InvertedIndex CreateIndex(IndexDefinition definition)
    {
        var index = new InvertedIndex(definition, _analyzer, _embeddingModel);
        if (!_indices.TryAdd(definition.Name, new SearchIndexHandle(index))) throw new InvalidOperationException($"Index '{definition.Name}' already exists.");
        return index;
    }

    public void Hydrate(IndexSnapshot snapshot)
    {
        var index = new InvertedIndex(snapshot.Definition, _analyzer, _embeddingModel);
        index.ImportSnapshot(snapshot);
        _indices[snapshot.Definition.Name] = new SearchIndexHandle(index);
        SearchTelemetry.SetIndexSize(index.GetStats().Documents);
    }

    public bool TryGetIndex(string nameOrAlias, out SearchIndexHandle? handle)
    {
        _gate.EnterReadLock();
        try
        {
            var resolved = _aliases.TryGetValue(nameOrAlias, out var target) ? target : nameOrAlias;
            return _indices.TryGetValue(resolved, out handle);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public SearchIndexHandle GetIndex(string nameOrAlias) => TryGetIndex(nameOrAlias, out var index) && index is not null
        ? index
        : throw new KeyNotFoundException($"Index or alias '{nameOrAlias}' was not found.");

    public IReadOnlyList<IndexStats> ListStats() => _indices.Values.Select(handle => handle.Index.GetStats()).OrderBy(stat => stat.Name, StringComparer.Ordinal).ToArray();

    public IReadOnlyDictionary<string, string> GetAliases()
    {
        _gate.EnterReadLock();
        try { return new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase); }
        finally { _gate.ExitReadLock(); }
    }

    public void SetAlias(string alias, string indexName)
    {
        if (!_indices.ContainsKey(indexName)) throw new KeyNotFoundException($"Index '{indexName}' was not found.");
        _gate.EnterWriteLock();
        try { _aliases[alias] = indexName; }
        finally { _gate.ExitWriteLock(); }
    }

    public void SwapAlias(string alias, string expectedCurrent, string next)
    {
        if (!_indices.ContainsKey(next)) throw new KeyNotFoundException($"Target index '{next}' was not found.");
        _gate.EnterWriteLock();
        try
        {
            if (!_aliases.TryGetValue(alias, out var current) || !string.Equals(current, expectedCurrent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Alias '{alias}' no longer points to expected index '{expectedCurrent}'.");
            }
            _aliases[alias] = next;
        }
        finally { _gate.ExitWriteLock(); }
    }

    public bool DropIndex(string name)
    {
        _gate.EnterWriteLock();
        try
        {
            if (_aliases.Values.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Cannot drop an index while an alias points at it.");
            return _indices.TryRemove(name, out _);
        }
        finally { _gate.ExitWriteLock(); }
    }
}

public sealed class SearchIndexService(SearchCluster cluster, IIndexStateStore stateStore, IClock clock)
{
    public async Task CreateAsync(IndexDefinition definition, string? alias = null, CancellationToken cancellationToken = default)
    {
        var index = cluster.CreateIndex(definition);
        index.MarkRefreshed(clock.UtcNow);
        await stateStore.SaveAsync(index.ExportSnapshot(), cancellationToken);
        if (!string.IsNullOrWhiteSpace(alias))
        {
            cluster.SetAlias(alias, definition.Name);
            await stateStore.SaveAliasAsync(alias, definition.Name, cancellationToken);
        }
    }

    public void EnqueueDocument(SearchDocument document)
    {
        var handle = cluster.GetIndex(document.IndexName);
        handle.Enqueue(new UpsertIndexOperation(document with { IndexName = handle.Index.Name }));
    }
    public void EnqueuePatch(string index, string documentId, DocumentPatch patch) => cluster.GetIndex(index).Enqueue(new PatchIndexOperation(documentId, patch));
    public void EnqueueDelete(string index, string documentId) => cluster.GetIndex(index).Enqueue(new DeleteIndexOperation(documentId));

    public async Task<int> RefreshAsync(string indexOrAlias, CancellationToken cancellationToken = default)
    {
        var handle = cluster.GetIndex(indexOrAlias);
        var changes = handle.Refresh(clock.UtcNow);
        if (changes > 0) await stateStore.SaveAsync(handle.Index.ExportSnapshot(), cancellationToken);
        return changes;
    }

    public async Task<int> RefreshDueAsync(CancellationToken cancellationToken = default)
    {
        var refreshed = 0;
        foreach (var stats in cluster.ListStats())
        {
            var handle = cluster.GetIndex(stats.Name);
            if (handle.QueuedCount == 0 || clock.UtcNow - stats.RefreshedAt < TimeSpan.FromMilliseconds(handle.Index.Definition.RefreshIntervalMilliseconds)) continue;
            refreshed += await RefreshAsync(stats.Name, cancellationToken);
        }
        return refreshed;
    }

    public async Task SwapAliasAsync(string alias, string expectedCurrent, string next, CancellationToken cancellationToken = default)
    {
        cluster.SwapAlias(alias, expectedCurrent, next);
        await stateStore.SaveAliasAsync(alias, next, cancellationToken);
    }

    public async Task CompactAsync(string indexOrAlias, CancellationToken cancellationToken = default)
    {
        var handle = cluster.GetIndex(indexOrAlias);
        handle.Index.Compact();
        await stateStore.SaveAsync(handle.Index.ExportSnapshot(), cancellationToken);
    }
    public async Task DropAsync(string index, CancellationToken cancellationToken = default)
    {
        if (cluster.DropIndex(index)) await stateStore.DeleteAsync(index, cancellationToken);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        foreach (var snapshot in await stateStore.LoadAsync(cancellationToken)) cluster.Hydrate(snapshot);
        foreach (var (alias, target) in await stateStore.LoadAliasesAsync(cancellationToken))
        {
            if (cluster.TryGetIndex(target, out _)) cluster.SetAlias(alias, target);
        }
    }
}
