using System.Collections.ObjectModel;

namespace EnterpriseSearch.Domain.Search;

public enum TokenizerKind
{
    Standard,
    Whitespace,
    NGram,
    EdgeNGram
}

public enum FacetKind
{
    Terms,
    Range,
    Hierarchical
}

public enum RetrievalMode
{
    Keyword,
    Vector,
    HybridRrf,
    HybridLinear
}

public sealed record AnalyzerDefinition(
    string Name,
    TokenizerKind Tokenizer = TokenizerKind.Standard,
    bool Lowercase = true,
    bool UnicodeNormalize = true,
    bool AccentFold = true,
    bool HtmlStrip = true,
    bool RemoveStopWords = true,
    bool Stem = true,
    bool ExpandSynonyms = false,
    bool Shingles = false,
    int MinGram = 2,
    int MaxGram = 15);

public sealed record FieldDefinition(
    string Name,
    string Analyzer = "standard",
    double Boost = 1d,
    bool Searchable = true,
    bool Facetable = false,
    bool IsNumeric = false,
    bool IsDate = false);

public sealed record IndexDefinition(
    string Name,
    IReadOnlyDictionary<string, FieldDefinition> Fields,
    IReadOnlyDictionary<string, AnalyzerDefinition>? Analyzers = null,
    int RefreshIntervalMilliseconds = 500)
{
    public AnalyzerDefinition GetAnalyzer(string field)
    {
        var analyzerName = Fields.TryGetValue(field, out var definition) ? definition.Analyzer : "standard";
        if (Analyzers is not null && Analyzers.TryGetValue(analyzerName, out var configured))
        {
            return configured;
        }

        return analyzerName.ToLowerInvariant() switch
        {
            "whitespace" => BuiltInAnalyzers.Whitespace,
            "keyword" => new AnalyzerDefinition("keyword", TokenizerKind.Whitespace, RemoveStopWords: false, Stem: false),
            "ngram" => new AnalyzerDefinition("ngram", TokenizerKind.NGram, RemoveStopWords: false, Stem: false),
            "edge" or "edge_ngram" => BuiltInAnalyzers.Edge,
            _ => BuiltInAnalyzers.Standard
        };
    }

    public double GetBoost(string field) => Fields.TryGetValue(field, out var definition) ? definition.Boost : 1d;
}

public static class BuiltInAnalyzers
{
    public static readonly AnalyzerDefinition Standard = new("standard");
    public static readonly AnalyzerDefinition Whitespace = new("whitespace", TokenizerKind.Whitespace);
    public static readonly AnalyzerDefinition Edge = new("edge", TokenizerKind.EdgeNGram, RemoveStopWords: false, Stem: false);
}

public sealed record SearchDocument
{
    public SearchDocument(
        string id,
        string indexName,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, decimal>? numericFields = null,
        IReadOnlyDictionary<string, DateTimeOffset>? dateFields = null,
        IReadOnlyCollection<string>? allowedGroups = null,
        DateTimeOffset createdAt = default,
        double popularity = 0d,
        bool isInStock = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        Id = id;
        IndexName = indexName;
        Fields = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase));
        NumericFields = new ReadOnlyDictionary<string, decimal>(new Dictionary<string, decimal>(numericFields ?? new Dictionary<string, decimal>(), StringComparer.OrdinalIgnoreCase));
        DateFields = new ReadOnlyDictionary<string, DateTimeOffset>(new Dictionary<string, DateTimeOffset>(dateFields ?? new Dictionary<string, DateTimeOffset>(), StringComparer.OrdinalIgnoreCase));
        AllowedGroups = (allowedGroups ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        CreatedAt = createdAt == default ? DateTimeOffset.UnixEpoch : createdAt;
        Popularity = popularity;
        IsInStock = isInStock;
    }

    public string Id { get; init; }
    public string IndexName { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; }
    public IReadOnlyDictionary<string, decimal> NumericFields { get; init; }
    public IReadOnlyDictionary<string, DateTimeOffset> DateFields { get; init; }
    public IReadOnlyCollection<string> AllowedGroups { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public double Popularity { get; init; }
    public bool IsInStock { get; init; }

    public bool IsAllowedFor(IReadOnlyCollection<string> groups) =>
        AllowedGroups.Count == 0 || groups.Any(group => AllowedGroups.Contains(group, StringComparer.OrdinalIgnoreCase));

    public IEnumerable<string> GetValues(string field)
    {
        if (Fields.TryGetValue(field, out var value))
        {
            return value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        return Array.Empty<string>();
    }

    public SearchDocument ApplyPatch(DocumentPatch patch) => new(
        Id,
        IndexName,
        patch.Fields is null ? Fields : Merge(Fields, patch.Fields),
        patch.NumericFields is null ? NumericFields : Merge(NumericFields, patch.NumericFields),
        patch.DateFields is null ? DateFields : Merge(DateFields, patch.DateFields),
        patch.AllowedGroups ?? AllowedGroups,
        patch.CreatedAt ?? CreatedAt,
        patch.Popularity ?? Popularity,
        patch.IsInStock ?? IsInStock);

    private static IReadOnlyDictionary<string, TValue> Merge<TValue>(IReadOnlyDictionary<string, TValue> existing, IReadOnlyDictionary<string, TValue> changed)
    {
        var result = new Dictionary<string, TValue>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in changed)
        {
            result[key] = value;
        }
        return result;
    }
}

public sealed record DocumentPatch(
    IReadOnlyDictionary<string, string>? Fields = null,
    IReadOnlyDictionary<string, decimal>? NumericFields = null,
    IReadOnlyDictionary<string, DateTimeOffset>? DateFields = null,
    IReadOnlyCollection<string>? AllowedGroups = null,
    DateTimeOffset? CreatedAt = null,
    double? Popularity = null,
    bool? IsInStock = null);

public sealed record AnalyzedToken(string Term, int Position, int StartOffset, int EndOffset, string? Type = null);

public sealed record Posting(int TermFrequency, IReadOnlyList<int> Positions, long DocumentVersion);

public sealed record FieldStatistics(int DocumentCount, long TotalLength)
{
    public double AverageLength => DocumentCount == 0 ? 0d : (double)TotalLength / DocumentCount;
}

public abstract record SearchClause;
public sealed record MatchAllClause : SearchClause;
public sealed record TermClause(string? Field, string Term) : SearchClause;
public sealed record PhraseClause(string? Field, IReadOnlyList<string> Terms, int Slop = 0, IReadOnlyList<int>? Positions = null) : SearchClause;
public sealed record PrefixClause(string? Field, string Prefix) : SearchClause;
public sealed record WildcardClause(string? Field, string Pattern) : SearchClause;
public sealed record FuzzyClause(string? Field, string Term, int MaxEdits = 2) : SearchClause;
public sealed record RangeClause(string Field, decimal? Lower, decimal? Upper, bool IncludeLower = true, bool IncludeUpper = true) : SearchClause;
public sealed record MultiFieldClause(string Text, IReadOnlyDictionary<string, double> Fields) : SearchClause;
public sealed record BooleanClause(
    IReadOnlyList<SearchClause>? Must = null,
    IReadOnlyList<SearchClause>? Should = null,
    IReadOnlyList<SearchClause>? MustNot = null,
    IReadOnlyList<SearchClause>? Filter = null,
    int MinimumShouldMatch = 0) : SearchClause;

public sealed record NumericRange(string Label, decimal? From, decimal? To, bool IncludeFrom = true, bool IncludeTo = false);
public sealed record FacetRequest(string Name, string Field, FacetKind Kind = FacetKind.Terms, IReadOnlyList<NumericRange>? Ranges = null, int Size = 10);

public sealed record SearchRequest(
    string Index,
    SearchClause? Query = null,
    string? QueryText = null,
    RetrievalMode Mode = RetrievalMode.Keyword,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Filters = null,
    IReadOnlyList<FacetRequest>? Facets = null,
    IReadOnlyCollection<string>? CallerGroups = null,
    int From = 0,
    int Size = 10,
    string? SearchAfter = null,
    bool Explain = false,
    IReadOnlyList<string>? HighlightFields = null,
    bool UseApproximateVector = false,
    bool CrossFeatureRerank = false);

public sealed record TermScoreExplanation(string Field, string Term, int TermFrequency, int DocumentFrequency, int FieldLength, double AverageFieldLength, double Idf, double Contribution);
public sealed record ScoreExplanation(IReadOnlyList<TermScoreExplanation> Terms, double FunctionMultiplier, double ClickBoost, string Summary);
public sealed record SearchHit(SearchDocument Document, double Score, ScoreExplanation? Explanation = null, IReadOnlyDictionary<string, string>? Highlights = null);
public sealed record FacetBucket(string Key, long Count);
public sealed record FacetResult(string Name, IReadOnlyList<FacetBucket> Buckets);
public sealed record SearchResponse(
    IReadOnlyList<SearchHit> Hits,
    long Total,
    IReadOnlyDictionary<string, FacetResult> Facets,
    string? DidYouMean,
    string? NextSearchAfter,
    TimeSpan Took);

public sealed record IndexStats(string Name, long Documents, long Tombstones, long Terms, long PostingCount, long Version, DateTimeOffset RefreshedAt);
public sealed record Suggestion(string Text, long DocumentFrequency);
public sealed record VectorHit(string DocumentId, double Score);
public sealed record VectorComparison(double RecallAtK, TimeSpan ExactLatency, TimeSpan ApproximateLatency, int ExactCandidates, int ApproximateCandidates);
public sealed record ClickEvent(string Index, string Query, string DocumentId, int Position, DateTimeOffset OccurredAt);
public sealed record QueryLog(string Index, string Query, int ResultCount, DateTimeOffset OccurredAt, IReadOnlyList<string>? ResultsShown = null);
public sealed record PositionCtr(int Position, long Impressions, long Clicks, double Ctr);
public sealed record AnalyticsSummary(double ZeroResultRate, IReadOnlyList<PositionCtr> CtrByPosition, IReadOnlyList<(string Query, long Count)> TopQueries);

public sealed record RelevanceJudgement(string Query, string DocumentId, int Grade, string Index = "catalogue");
public sealed record EvaluationMetrics(double NdcgAt10, double Mrr, double PrecisionAt10, double RecallAt10);
public sealed record EvaluationReport(IReadOnlyDictionary<RetrievalMode, EvaluationMetrics> Metrics, int QueryCount, DateTimeOffset RanAt);

public class QueryValidationException : Exception
{
    public QueryValidationException(string message) : base(message) { }
}

public sealed class QueryParseException : QueryValidationException
{
    public QueryParseException(string message) : base(message) { }
}
