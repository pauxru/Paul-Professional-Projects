using System.Security.Claims;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Api.Contracts;

public sealed record CreateIndexRequest(
    string Name,
    Dictionary<string, FieldDefinition>? Fields = null,
    Dictionary<string, AnalyzerDefinition>? Analyzers = null,
    string? Alias = null,
    int RefreshIntervalMilliseconds = 500)
{
    public IndexDefinition ToDefinition() => new(Name,
        Fields is { Count: > 0 } ? new Dictionary<string, FieldDefinition>(Fields, StringComparer.OrdinalIgnoreCase) : DefaultFields(),
        Analyzers is null ? null : new Dictionary<string, AnalyzerDefinition>(Analyzers, StringComparer.OrdinalIgnoreCase),
        RefreshIntervalMilliseconds);

    private static IReadOnlyDictionary<string, FieldDefinition> DefaultFields() => new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = new("title", "standard", 3d, true, true),
        ["body"] = new("body", "standard", 1d, true, true)
    };
}

public sealed record AliasRequest(string Alias);
public sealed record AliasSwapRequest(string Alias, string ExpectedCurrent, string Next);

public sealed record DocumentRequest(
    string Id,
    Dictionary<string, string> Fields,
    Dictionary<string, decimal>? NumericFields = null,
    Dictionary<string, DateTimeOffset>? DateFields = null,
    string[]? AllowedGroups = null,
    DateTimeOffset? CreatedAt = null,
    double Popularity = 0d,
    bool IsInStock = true)
{
    public SearchDocument ToDomain(string index) => new(Id, index, Fields, NumericFields, DateFields, AllowedGroups, CreatedAt ?? DateTimeOffset.UnixEpoch, Popularity, IsInStock);
    public string? Validate() => string.IsNullOrWhiteSpace(Id) ? "id is required." : Fields is null || Fields.Count == 0 ? "At least one text field is required." : null;
}

public sealed record PatchRequest(
    Dictionary<string, string>? Fields = null,
    Dictionary<string, decimal>? NumericFields = null,
    Dictionary<string, DateTimeOffset>? DateFields = null,
    string[]? AllowedGroups = null,
    DateTimeOffset? CreatedAt = null,
    double? Popularity = null,
    bool? IsInStock = null)
{
    public DocumentPatch ToDomain() => new(Fields, NumericFields, DateFields, AllowedGroups, CreatedAt, Popularity, IsInStock);
}

public sealed record SearchRequestDto(
    string Index = "catalogue",
    string? Query = null,
    RetrievalMode Mode = RetrievalMode.Keyword,
    Dictionary<string, double>? MultiFields = null,
    Dictionary<string, string[]>? Filters = null,
    FacetRequest[]? Facets = null,
    int From = 0,
    int Size = 10,
    string? SearchAfter = null,
    bool Explain = false,
    string[]? HighlightFields = null,
    bool UseApproximateVector = false,
    bool CrossFeatureRerank = false)
{
    public SearchRequest ToDomain(ClaimsPrincipal user) => new(
        Index,
        Query: MultiFields is { Count: > 0 } ? new MultiFieldClause(Query ?? string.Empty, MultiFields) : null,
        QueryText: Query,
        Mode: Mode,
        Filters: Filters?.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase),
        Facets: Facets,
        CallerGroups: user.FindAll("groups").Select(claim => claim.Value).ToArray(),
        From: From,
        Size: Size,
        SearchAfter: SearchAfter,
        Explain: Explain,
        HighlightFields: HighlightFields,
        UseApproximateVector: UseApproximateVector,
        CrossFeatureRerank: CrossFeatureRerank);
}

public sealed record AnalyzeRequest(string Text, string? Analyzer = null, string? Index = null, string? Field = null);
public sealed record ClickRequest(string Index, string Query, string DocumentId, int Position);
public sealed record TokenRequest(string Subject = "demo-user", string[]? Scopes = null, string[]? Groups = null);
