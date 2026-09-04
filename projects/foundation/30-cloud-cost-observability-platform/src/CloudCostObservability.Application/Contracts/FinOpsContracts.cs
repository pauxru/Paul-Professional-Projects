using System.ComponentModel.DataAnnotations;
using CloudCostObservability.Domain.Models;
using CloudCostObservability.Domain.Services;

namespace CloudCostObservability.Application.Contracts;

public interface IClock
{
    DateOnly Today { get; }
    DateTimeOffset UtcNow { get; }
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (decimal)PageSize);
}

public sealed record ResourceQuery(string? Team = null, string? Subscription = null, string? Category = null, int Page = 1, int PageSize = 50);
public sealed record CostQuery(DateOnly? From = null, DateOnly? To = null, string? Team = null, string? Service = null, string? GroupBy = "day", string? Basis = "amortized", string? ReportingCurrency = "USD", int Page = 1, int PageSize = 100);
public sealed record CostGroup(string Key, decimal ActualCost, decimal AmortizedCost, decimal UsageQuantity, int RecordCount);
public sealed record CostQueryResult(PageResult<CostGroup> Results, decimal TotalActualCost, decimal TotalAmortizedCost, string Currency);
public sealed record AllocationQuery(DateOnly? From = null, DateOnly? To = null, string? Team = null, string? Basis = "amortized", int Page = 1, int PageSize = 100);
public sealed record AllocationReport(PageResult<AllocationLine> Lines, decimal SourceTotal, decimal AllocatedTotal, decimal UnallocatedTotal, bool InvariantHolds);
public sealed record AllocationRuleRequest(
    int Order,
    AllocationMethod Method,
    string? MatchKey,
    string? MatchValue,
    string? TargetTeam,
    string? TargetCostCentre,
    SharedSplitStrategy? SplitStrategy,
    Dictionary<string, decimal>? FixedPercentages,
    bool Enabled = true);

public sealed record CostImportLine(
    string BillingPeriod,
    string ResourceId,
    DateOnly UsageDate,
    string Meter,
    string Service,
    ResourceCategory Category,
    decimal UsageQuantity,
    string Unit,
    decimal Rate,
    decimal ActualCost,
    decimal AmortizedCost,
    decimal Credits,
    decimal Discounts,
    decimal ReservedCoveragePercent,
    decimal ReservedUtilizationPercent,
    string Currency = "USD",
    bool IsHourly = false,
    int? Hour = null);

public interface ICostDataSource
{
    ImportProvider Provider { get; }
    IAsyncEnumerable<CostImportLine> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed record ImportResult(ImportProvider Provider, int Inserted, int Restated, int Rejected, DateTimeOffset CompletedAt);
public sealed record ImportHistory(string Id, ImportProvider Provider, int Inserted, int Restated, int Rejected, DateTimeOffset CompletedAt);
public sealed record FileImportRequest([property: Required] string Provider, [property: Required] string Path);

public sealed record CreateBudgetRequest(
    [property: Required] string Id,
    BudgetScope Scope,
    [property: Required] string Selector,
    [property: Range(typeof(decimal), "0.01", "999999999")] decimal Amount,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    BudgetCadence Cadence = BudgetCadence.Monthly);

public sealed record RecommendationActionRequest([property: Required] string Action, decimal? BaselineMonthlyCost = null, decimal? PostChangeMonthlyCost = null);
public sealed record AnomalyActionRequest([property: Required] string Action);

public interface IFinOpsService
{
    Task SeedSyntheticDataAsync(CancellationToken cancellationToken = default);
    Task<PageResult<CloudResource>> GetResourcesAsync(ResourceQuery query, string? scopedTeam, CancellationToken cancellationToken = default);
    Task<CostQueryResult> GetCostsAsync(CostQuery query, string? scopedTeam, CancellationToken cancellationToken = default);
    Task<AllocationReport> GetAllocationsAsync(AllocationQuery query, string? scopedTeam, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AllocationRule>> GetAllocationRulesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AllocationRule>> ReplaceAllocationRulesAsync(IEnumerable<AllocationRuleRequest> rules, CancellationToken cancellationToken = default);
    Task<ImportResult> ImportAsync(ICostDataSource source, CancellationToken cancellationToken = default);
    Task<ImportResult> ImportFileAsync(FileImportRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImportHistory>> GetImportsAsync(CancellationToken cancellationToken = default);
    Task<TagCoverageReport> GetTagCoverageAsync(string? scopedTeam, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetDefinition>> GetBudgetsAsync(CancellationToken cancellationToken = default);
    Task<BudgetDefinition> CreateBudgetAsync(CreateBudgetRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetStatus>> GetBudgetStatusesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForecastResult>> GetForecastsAsync(string? team, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnomalyEvent>> GetAnomaliesAsync(bool includeSuppressed, CancellationToken cancellationToken = default);
    Task<AnomalyEvent?> UpdateAnomalyAsync(string id, AnomalyActionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Recommendation>> GetRecommendationsAsync(CancellationToken cancellationToken = default);
    Task<Recommendation?> UpdateRecommendationAsync(string id, RecommendationActionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UnitEconomicsPoint>> GetUnitEconomicsAsync(string? scopedTeam, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnomalyGroup>> GetAnomalyGroupsAsync(CancellationToken cancellationToken = default);
}
