using System.Diagnostics;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public sealed record SearchBenchmarkReport(int Documents, TimeSpan IndexingTime, double DocumentsPerSecond, VectorComparison VectorComparison, TimeSpan HundredKeywordQueries);

public sealed class SearchBenchmark(SearchCluster cluster, SearchEngine engine, IEmbeddingModel embedding)
{
    public SearchBenchmarkReport Run(CancellationToken cancellationToken = default)
    {
        var name = "benchmark-" + Guid.NewGuid().ToString("N");
        var fields = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = new("title", "standard", 3d, true),
            ["body"] = new("body", "standard", 1d, true)
        };
        var index = cluster.CreateIndex(new IndexDefinition(name, fields));
        var stopwatch = Stopwatch.StartNew();
        var topics = new[] { "laptop", "television", "smartphone", "headphones", "camera", "printer", "router", "keyboard", "monitor", "tablet" };
        for (var number = 0; number < 5_000; number++)
        {
            var topic = topics[number % topics.Length];
            index.Upsert(new SearchDocument($"benchmark-{number}", name, new Dictionary<string, string>
            {
                ["title"] = $"Contoso {topic} retail product {number}",
                ["body"] = $"Synthetic {topic} benchmark document for deterministic hybrid retrieval"
            }, createdAt: DateTimeOffset.UnixEpoch));
        }
        stopwatch.Stop();
        var indexing = stopwatch.Elapsed;
        var vectorQuery = embedding.Embed("contoso laptop retail product 42");
        index.ApproximateVectorIndex.Search(vectorQuery, 1, cancellationToken);
        var comparison = VectorSearchMeasurement.Compare(index.ExactVectorIndex, index.ApproximateVectorIndex, vectorQuery, 10, cancellationToken);
        stopwatch.Restart();
        for (var iteration = 0; iteration < 100; iteration++) engine.Search(new SearchRequest(name, QueryText: "laptop", Size: 10), cancellationToken);
        stopwatch.Stop();
        cluster.DropIndex(name);
        return new SearchBenchmarkReport(5_000, indexing, 5_000d / Math.Max(0.001d, indexing.TotalSeconds), comparison, stopwatch.Elapsed);
    }
}
