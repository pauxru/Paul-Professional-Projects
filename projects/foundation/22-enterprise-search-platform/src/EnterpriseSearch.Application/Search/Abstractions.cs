using System.ComponentModel.DataAnnotations;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ISearchAnalytics
{
    void LogQuery(QueryLog query);
    void LogClick(ClickEvent click);
    double GetClickBoost(string index, string query, string documentId);
    AnalyticsSummary GetSummary();
}

public interface IIndexStateStore
{
    Task<IReadOnlyList<IndexSnapshot>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default);
    Task DeleteAsync(string indexName, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> LoadAliasesAsync(CancellationToken cancellationToken = default);
    Task SaveAliasAsync(string alias, string indexName, CancellationToken cancellationToken = default);
}

public sealed class QueryLimits
{
    public const string SectionName = "Search";
    [Range(1, 100)] public int MaxPageSize { get; init; } = 50;
    [Range(0, 10_000)] public int MaxFrom { get; init; } = 1_000;
    [Range(1, 256)] public int MaxClauses { get; init; } = 32;
    [Range(1, 500)] public int MaxWildcardExpansion { get; init; } = 50;
    [Range(1, 10000)] public int MaxFuzzyCandidates { get; init; } = 2_000;
    [Range(1, 5000)] public int QueryTimeoutMilliseconds { get; init; } = 750;
}

public sealed class Bm25Options
{
    public const string SectionName = "Bm25";
    [Range(0.1, 5)] public double K1 { get; init; } = 1.2;
    [Range(0, 1)] public double B { get; init; } = 0.75;
}

public sealed class FunctionScoreOptions
{
    public const string SectionName = "FunctionScore";
    [Range(1, 3650)] public double RecencyHalfLifeDays { get; init; } = 90;
    [Range(0, 10)] public double RecencyWeight { get; init; } = 0.15;
    [Range(0, 10)] public double PopularityWeight { get; init; } = 0.05;
    [Range(1, 10)] public double InStockMultiplier { get; init; } = 1.1;
    [Range(0, 10)] public double ClickWeight { get; init; } = 0.2;
}

public sealed record FunctionScoreBreakdown(double Multiplier, double ClickBoost, string Summary);

public interface IScorer
{
    TermScoreExplanation ScoreTerm(InvertedIndex index, string documentId, string field, string term, Posting posting, double additionalBoost = 1d);
}
