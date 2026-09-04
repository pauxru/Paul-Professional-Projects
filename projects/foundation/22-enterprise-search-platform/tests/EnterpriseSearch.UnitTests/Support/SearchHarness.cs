using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.UnitTests.Support;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal sealed class InMemoryIndexStateStore : IIndexStateStore
{
    private readonly Dictionary<string, IndexSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    public Task<IReadOnlyList<IndexSnapshot>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IndexSnapshot>>(_snapshots.Values.ToArray());
    public Task SaveAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default) { _snapshots[snapshot.Definition.Name] = snapshot; return Task.CompletedTask; }
    public Task DeleteAsync(string indexName, CancellationToken cancellationToken = default) { _snapshots.Remove(indexName); return Task.CompletedTask; }
    public Task<IReadOnlyDictionary<string, string>> LoadAliasesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase));
    public Task SaveAliasAsync(string alias, string indexName, CancellationToken cancellationToken = default) { _aliases[alias] = indexName; return Task.CompletedTask; }
}

internal sealed class SearchHarness
{
    public SearchHarness(QueryLimits? limits = null, Bm25Options? bm25 = null, FunctionScoreOptions? function = null)
    {
        Clock = new FakeClock(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        Analyzer = new TextAnalyzer();
        Cluster = new SearchCluster(Analyzer, new DeterministicEmbeddingModel(Analyzer));
        Store = new InMemoryIndexStateStore();
        Limits = limits ?? new QueryLimits();
        Analytics = new InMemorySearchAnalytics();
        Engine = new SearchEngine(Cluster, new QueryStringParser(Limits), new Bm25Scorer(bm25 ?? new Bm25Options()), new FunctionScorer(function ?? new FunctionScoreOptions(), Clock, Analytics), Analytics, Clock, Limits);
        Service = new SearchIndexService(Cluster, Store, Clock);
    }

    public FakeClock Clock { get; }
    public TextAnalyzer Analyzer { get; }
    public SearchCluster Cluster { get; }
    public InMemoryIndexStateStore Store { get; }
    public QueryLimits Limits { get; }
    public InMemorySearchAnalytics Analytics { get; }
    public SearchEngine Engine { get; }
    public SearchIndexService Service { get; }

    public InvertedIndex CreateIndex(string name = "test", string? alias = null, IReadOnlyDictionary<string, FieldDefinition>? fields = null)
    {
        var definition = new IndexDefinition(name, fields ?? DefaultFields());
        var index = Cluster.CreateIndex(definition);
        if (alias is not null) Cluster.SetAlias(alias, name);
        return index;
    }

    public SearchDocument Document(string id, string title, string body = "", string category = "general", decimal price = 0m, IReadOnlyCollection<string>? groups = null, DateTimeOffset? created = null, double popularity = 0d, bool inStock = true, string index = "test") =>
        new(id, index, new Dictionary<string, string> { ["title"] = title, ["body"] = body, ["category"] = category }, new Dictionary<string, decimal> { ["price"] = price }, allowedGroups: groups, createdAt: created ?? Clock.UtcNow, popularity: popularity, isInStock: inStock);

    public static IReadOnlyDictionary<string, FieldDefinition> DefaultFields() => new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = new("title", "standard", 3d, true, true),
        ["body"] = new("body", "standard", 1d, true, true),
        ["category"] = new("category", "keyword", 1d, true, true),
        ["price"] = new("price", "keyword", 0d, false, true, true)
    };
}
