using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Infrastructure.Search;

public sealed class ContosoDemoSeeder(SearchCluster cluster, SearchIndexService indices, IClock clock)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!cluster.TryGetIndex("catalogue", out _))
        {
            await indices.CreateAsync(CreateCatalogueDefinition(), "catalogue", cancellationToken);
            foreach (var document in CreateCatalogueDocuments()) indices.EnqueueDocument(document);
            await indices.RefreshAsync("catalogue", cancellationToken);
        }
        if (!cluster.TryGetIndex("knowledge", out _))
        {
            await indices.CreateAsync(CreateKnowledgeDefinition(), "knowledge", cancellationToken);
            foreach (var document in CreateKnowledgeDocuments()) indices.EnqueueDocument(document);
            await indices.RefreshAsync("knowledge", cancellationToken);
        }
    }

    public static IndexDefinition CreateCatalogueDefinition() => new(
        "catalogue-v1",
        new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = new("title", "retail_text", 3d, true, true),
            ["description"] = new("description", "retail_text", 1d, true),
            ["brand"] = new("brand", "keyword", 1.2d, true, true),
            ["category"] = new("category", "keyword", 0.8d, true, true),
            ["sku"] = new("sku", "keyword", 1.5d, true),
            ["price"] = new("price", "keyword", 0d, false, true, true)
        },
        new Dictionary<string, AnalyzerDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["retail_text"] = new("retail_text", ExpandSynonyms: true),
            ["keyword"] = new("keyword", TokenizerKind.Whitespace, RemoveStopWords: false, Stem: false)
        });

    public static IndexDefinition CreateKnowledgeDefinition() => new(
        "knowledge-v1",
        new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = new("title", "support_text", 3d, true, true),
            ["body"] = new("body", "support_text", 1d, true),
            ["productLine"] = new("productLine", "keyword", 1d, true, true),
            ["topic"] = new("topic", "keyword", 1d, true, true)
        },
        new Dictionary<string, AnalyzerDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["support_text"] = new("support_text", ExpandSynonyms: true, Shingles: true),
            ["keyword"] = new("keyword", TokenizerKind.Whitespace, RemoveStopWords: false, Stem: false)
        });

    private IEnumerable<SearchDocument> CreateCatalogueDocuments()
    {
        var themes = new[] { "laptop", "notebook", "television", "smartphone", "headphones", "monitor", "keyboard", "camera", "printer", "router" };
        var brands = new[] { "Contoso", "Fabrikam", "Northwind", "Adventure", "Tailspin" };
        var categories = new[] { "electronics/laptops", "electronics/televisions", "electronics/mobile", "electronics/audio", "office/peripherals" };
        for (var number = 1; number <= 30; number++)
        {
            var theme = themes[(number - 1) % themes.Length];
            yield return new SearchDocument(
                $"catalogue-golden-{number:D2}", "catalogue-v1",
                new Dictionary<string, string>
                {
                    ["title"] = $"Contoso Retail {theme} qtoken{number:D2}",
                    ["description"] = $"Fictional Contoso Retail signature {theme} with durable support and warranty coverage.",
                    ["brand"] = "Contoso",
                    ["category"] = categories[(number - 1) % categories.Length],
                    ["sku"] = $"CTR-QT-{number:D2}"
                },
                new Dictionary<string, decimal> { ["price"] = 100m + number * 25m },
                createdAt: clock.UtcNow.AddDays(-number), popularity: number * 3, isInStock: number % 7 != 0);
        }
        for (var number = 31; number <= 5_000; number++)
        {
            var theme = themes[number % themes.Length];
            var brand = brands[number % brands.Length];
            yield return new SearchDocument(
                $"catalogue-{number:D5}", "catalogue-v1",
                new Dictionary<string, string>
                {
                    ["title"] = $"{brand} {theme} retail item {number}",
                    ["description"] = $"Fictional Contoso Retail catalogue item: reliable {theme} for home and office workflows.",
                    ["brand"] = brand,
                    ["category"] = categories[number % categories.Length],
                    ["sku"] = $"CTR-{number:D6}"
                },
                new Dictionary<string, decimal> { ["price"] = 49m + (number % 20) * 50m },
                createdAt: clock.UtcNow.AddDays(-(number % 365)), popularity: number % 100, isInStock: number % 11 != 0);
        }
    }

    private IEnumerable<SearchDocument> CreateKnowledgeDocuments()
    {
        var topics = new[] { "returns", "shipping", "warranty", "account", "troubleshooting" };
        for (var number = 1; number <= 1_000; number++)
        {
            var topic = topics[number % topics.Length];
            yield return new SearchDocument(
                $"kb-{number:D4}", "knowledge-v1",
                new Dictionary<string, string>
                {
                    ["title"] = $"Contoso Retail {topic} guide {number}",
                    ["body"] = $"Fictional support knowledge article explaining {topic}, product setup, notebook and laptop assistance.",
                    ["productLine"] = number % 2 == 0 ? "consumer" : "business",
                    ["topic"] = topic
                },
                allowedGroups: number % 50 == 0 ? ["support-agent"] : Array.Empty<string>(),
                createdAt: clock.UtcNow.AddDays(-(number % 180)), popularity: number % 50);
        }
    }
}
