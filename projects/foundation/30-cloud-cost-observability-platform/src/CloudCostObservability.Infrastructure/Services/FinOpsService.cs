using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudCostObservability.Application.Contracts;
using CloudCostObservability.Domain.Models;
using CloudCostObservability.Domain.Services;
using CloudCostObservability.Infrastructure.Ingestion;
using CloudCostObservability.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudCostObservability.Infrastructure.Services;

public sealed class FinOpsService(
    FinOpsDbContext db,
    SyntheticFinOpsData syntheticData,
    IClock clock,
    AllocationEngine allocationEngine,
    TagGovernanceService tagGovernance,
    BudgetService budgetService,
    ForecastingService forecastingService,
    AnomalyDetectionService anomalyDetection,
    RecommendationEngine recommendationEngine,
    UnitEconomicsService unitEconomics,
    IOptions<CostDataOptions> costDataOptions) : IFinOpsService
{
    private readonly FinOpsDbContext _db = db;
    private readonly SyntheticFinOpsData _syntheticData = syntheticData;
    private readonly IClock _clock = clock;
    private readonly AllocationEngine _allocationEngine = allocationEngine;
    private readonly TagGovernanceService _tagGovernance = tagGovernance;
    private readonly BudgetService _budgetService = budgetService;
    private readonly ForecastingService _forecastingService = forecastingService;
    private readonly AnomalyDetectionService _anomalyDetection = anomalyDetection;
    private readonly RecommendationEngine _recommendationEngine = recommendationEngine;
    private readonly UnitEconomicsService _unitEconomics = unitEconomics;
    private readonly CostDataOptions _costDataOptions = costDataOptions.Value;

    public async Task SeedSyntheticDataAsync(CancellationToken cancellationToken = default)
    {
        await _db.Database.EnsureCreatedAsync(cancellationToken);
        if (!await _db.Resources.AnyAsync(cancellationToken))
        {
            await _db.Resources.AddRangeAsync(_syntheticData.GenerateResources(), cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        if (!await _db.FxRates.AnyAsync(cancellationToken))
        {
            var rates = new List<FxRate>();
            for (var date = SyntheticFinOpsData.DefaultStart; date <= SyntheticFinOpsData.DefaultEnd; date = date.AddDays(1))
                rates.Add(new FxRate(date, "USD", "KES", 125m + (date.DayNumber % 17) * .11m));
            await _db.FxRates.AddRangeAsync(rates, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        if (!await _db.AllocationRules.AnyAsync(cancellationToken))
        {
            _db.AllocationRules.AddRange(DefaultRules().Select(ToRow));
            await _db.SaveChangesAsync(cancellationToken);
        }
        if (!await _db.Budgets.AnyAsync(cancellationToken))
        {
            _db.Budgets.AddRange(
                new BudgetDefinition("budget-commerce-prod", BudgetScope.Team, "commerce", 21_000m, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)),
                new BudgetDefinition("budget-shared-subscription", BudgetScope.Subscription, "ns-shared-004", 18_000m, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)),
                new BudgetDefinition("budget-prod", BudgetScope.Environment, "production", 90_000m, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)));
            await _db.SaveChangesAsync(cancellationToken);
        }
        if (!await _db.Costs.AnyAsync(cancellationToken))
        {
            if (_costDataOptions.Provider.Equals("Synthetic", StringComparison.OrdinalIgnoreCase))
                await ImportAsync(_syntheticData.CreateCostSource(await _db.Resources.AsNoTracking().ToListAsync(cancellationToken)), cancellationToken);
            else if (_costDataOptions.Provider.Equals("AzureCostManagementExport", StringComparison.OrdinalIgnoreCase))
                await ImportAsync(new AzureCostManagementCsvDataSource(_costDataOptions.AzureExportPath), cancellationToken);
            else
                await ImportAsync(new AwsCurCsvDataSource(_costDataOptions.AwsCurPath), cancellationToken);
        }
        if (!await _db.BusinessMetrics.AnyAsync(cancellationToken))
        {
            var metrics = _syntheticData.GenerateBusinessMetrics(SyntheticFinOpsData.DefaultStart, SyntheticFinOpsData.DefaultEnd).Select(BusinessMetricRow.From);
            await _db.BusinessMetrics.AddRangeAsync(metrics, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        if (!await _db.Anomalies.AnyAsync(cancellationToken))
            await GenerateAnomaliesAsync(cancellationToken);
        if (!await _db.Recommendations.AnyAsync(cancellationToken))
            await GenerateRecommendationsAsync(cancellationToken);
    }

    public async Task<PageResult<CloudResource>> GetResourcesAsync(ResourceQuery query, string? scopedTeam, CancellationToken cancellationToken = default)
    {
        var page = NormalizePage(query.Page);
        var pageSize = NormalizePageSize(query.PageSize);
        var resources = await _db.Resources.AsNoTracking().ToListAsync(cancellationToken);
        var filtered = resources
            .Where(resource => query.Subscription is null || string.Equals(resource.SubscriptionId, query.Subscription, StringComparison.OrdinalIgnoreCase))
            .Where(resource => query.Category is null || string.Equals(resource.Category.ToString(), query.Category, StringComparison.OrdinalIgnoreCase))
            .Where(resource => TeamMatches(resource, scopedTeam ?? query.Team))
            .OrderBy(resource => resource.Id, StringComparer.Ordinal)
            .ToList();
        return Page(filtered, page, pageSize);
    }

    public async Task<CostQueryResult> GetCostsAsync(CostQuery query, string? scopedTeam, CancellationToken cancellationToken = default)
    {
        var (from, to) = ResolveRange(query.From, query.To);
        var resources = await _db.Resources.AsNoTracking().ToListAsync(cancellationToken);
        var resourceById = resources.ToDictionary(resource => resource.Id, StringComparer.OrdinalIgnoreCase);
        var costs = await _db.Costs.AsNoTracking()
            .Where(cost => cost.UsageDate >= from && cost.UsageDate <= to)
            .ToListAsync(cancellationToken);
        var requestedTeam = scopedTeam ?? query.Team;
        var filtered = costs
            .Where(cost => query.Service is null || string.Equals(cost.Service, query.Service, StringComparison.OrdinalIgnoreCase))
            .Where(cost => resourceById.TryGetValue(cost.ResourceId, out var resource) && TeamMatches(resource, requestedTeam))
            .ToList();
        var rate = await ReportingRateAsync(query.ReportingCurrency, cancellationToken);
        var groups = filtered
            .GroupBy(cost => GroupKey(cost, resourceById[cost.ResourceId], query.GroupBy))
            .Select(group => new CostGroup(
                group.Key,
                Round(group.Sum(cost => Convert(cost.EffectiveActualCost, rate))),
                Round(group.Sum(cost => Convert(cost.AmortizedCost - cost.Credits - cost.Discounts, rate))),
                group.Sum(cost => cost.UsageQuantity),
                group.Count()))
            .OrderByDescending(group => group.AmortizedCost)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        return new CostQueryResult(
            Page(groups, NormalizePage(query.Page), NormalizePageSize(query.PageSize)),
            Round(filtered.Sum(cost => Convert(cost.EffectiveActualCost, rate))),
            Round(filtered.Sum(cost => Convert(cost.AmortizedCost - cost.Credits - cost.Discounts, rate))),
            string.Equals(query.ReportingCurrency, "KES", StringComparison.OrdinalIgnoreCase) ? "KES" : "USD");
    }

    public async Task<AllocationReport> GetAllocationsAsync(AllocationQuery query, string? scopedTeam, CancellationToken cancellationToken = default)
    {
        var (from, to) = ResolveRange(query.From, query.To);
        var costs = await _db.Costs.AsNoTracking().Where(cost => cost.UsageDate >= from && cost.UsageDate <= to).ToListAsync(cancellationToken);
        var resources = await _db.Resources.AsNoTracking().ToListAsync(cancellationToken);
        var rules = await GetAllocationRulesAsync(cancellationToken);
        var basis = string.Equals(query.Basis, "actual", StringComparison.OrdinalIgnoreCase) ? CostBasis.Actual : CostBasis.Amortized;
        var audits = _allocationEngine.Allocate(costs, resources, rules, basis);
        var lines = audits.SelectMany(audit => audit.Lines).ToList();
        if (!string.IsNullOrWhiteSpace(scopedTeam))
            lines = lines.Where(line => string.Equals(line.Team, scopedTeam, StringComparison.OrdinalIgnoreCase)).ToList();
        else if (!string.IsNullOrWhiteSpace(query.Team))
            lines = lines.Where(line => string.Equals(line.Team, query.Team, StringComparison.OrdinalIgnoreCase)).ToList();

        var sourceTotal = lines.Sum(line => line.Amount);
        var unallocated = lines.Where(line => line.Method == AllocationMethod.Unallocated).Sum(line => line.Amount);
        var allocated = sourceTotal - unallocated;
        return new AllocationReport(
            Page(lines.OrderByDescending(line => line.Amount).ThenBy(line => line.CostRecordId, StringComparer.Ordinal).ToList(), NormalizePage(query.Page), NormalizePageSize(query.PageSize)),
            sourceTotal,
            allocated,
            unallocated,
            sourceTotal == allocated + unallocated);
    }

    public async Task<IReadOnlyList<AllocationRule>> GetAllocationRulesAsync(CancellationToken cancellationToken = default) =>
        (await _db.AllocationRules.AsNoTracking().OrderBy(rule => rule.Order).ToListAsync(cancellationToken)).Select(ToDomain).ToList();

    public async Task<IReadOnlyList<AllocationRule>> ReplaceAllocationRulesAsync(IEnumerable<AllocationRuleRequest> rules, CancellationToken cancellationToken = default)
    {
        var input = rules.OrderBy(rule => rule.Order).ToList();
        if (input.Select(rule => rule.Order).Distinct().Count() != input.Count)
            throw new ArgumentException("Allocation rule order must be unique.", nameof(rules));
        if (input.Any(rule => rule.Method == AllocationMethod.SharedFixed && (rule.FixedPercentages?.Values.Sum() ?? 0m) > 100m))
            throw new ArgumentException("Fixed shared-cost percentages cannot exceed 100.", nameof(rules));
        _db.AllocationRules.RemoveRange(_db.AllocationRules);
        _db.AllocationRules.AddRange(input.Select(rule => ToRow(new AllocationRule(rule.Order, rule.Method, rule.MatchKey, rule.MatchValue, rule.TargetTeam, rule.TargetCostCentre, rule.SplitStrategy, rule.FixedPercentages, rule.Enabled))));
        await WriteAuditAsync("system", "allocation-rules.replaced", "allocation-rules", "[]", JsonSerializer.Serialize(input), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return await GetAllocationRulesAsync(cancellationToken);
    }

    public async Task<ImportResult> ImportAsync(ICostDataSource source, CancellationToken cancellationToken = default)
    {
        await _db.Database.EnsureCreatedAsync(cancellationToken);
        var knownResources = (await _db.Resources.AsNoTracking().Select(resource => resource.Id).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batch = new List<CostRecord>(2_000);
        var inserted = 0;
        var restated = 0;
        var rejected = 0;
        await foreach (var line in source.ReadAsync(cancellationToken))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line.ResourceId) || !knownResources.Contains(line.ResourceId))
                {
                    rejected++;
                    continue;
                }
                batch.Add(new CostRecord(line.BillingPeriod, line.ResourceId, line.UsageDate, line.Meter, line.Service, line.Category, line.UsageQuantity, line.Unit, line.Rate, line.ActualCost, line.AmortizedCost, line.Credits, line.Discounts, line.ReservedCoveragePercent, line.ReservedUtilizationPercent, line.IsHourly, line.Hour, line.Currency, _clock.UtcNow));
                if (batch.Count >= 2_000)
                    await UpsertBatchAsync(batch, cancellationToken, count => inserted += count.Inserted, count => restated += count.Restated);
            }
            catch (ArgumentException)
            {
                rejected++;
            }
            catch (FormatException)
            {
                rejected++;
            }
        }
        if (batch.Count > 0)
            await UpsertBatchAsync(batch, cancellationToken, count => inserted += count.Inserted, count => restated += count.Restated);
        var completed = _clock.UtcNow;
        _db.ImportRuns.Add(new ImportRun { Provider = source.Provider, Inserted = inserted, Restated = restated, Rejected = rejected, CompletedAt = completed });
        await _db.SaveChangesAsync(cancellationToken);
        return new ImportResult(source.Provider, inserted, restated, rejected, completed);
    }

    public async Task<ImportResult> ImportFileAsync(FileImportRequest request, CancellationToken cancellationToken = default)
    {
        if (!System.IO.Path.GetExtension(request.Path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only CSV exports are accepted.", nameof(request));
        ICostDataSource source = request.Provider.ToLowerInvariant() switch
        {
            "azure" or "azurecostmanagementexport" or "azurecostmanagement" => new AzureCostManagementCsvDataSource(request.Path),
            "aws" or "awscur" => new AwsCurCsvDataSource(request.Path),
            _ => throw new ArgumentException("Provider must be azure or aws.", nameof(request))
        };
        return await ImportAsync(source, cancellationToken);
    }

    public async Task<IReadOnlyList<ImportHistory>> GetImportsAsync(CancellationToken cancellationToken = default) =>
        (await _db.ImportRuns.AsNoTracking().ToListAsync(cancellationToken))
            .OrderByDescending(run => run.CompletedAt)
            .Select(run => new ImportHistory(run.Id, run.Provider, run.Inserted, run.Restated, run.Rejected, run.CompletedAt))
            .ToList();

    public async Task<TagCoverageReport> GetTagCoverageAsync(string? scopedTeam, CancellationToken cancellationToken = default)
    {
        var resources = await _db.Resources.AsNoTracking().ToListAsync(cancellationToken);
        return _tagGovernance.BuildCoverage(resources.Where(resource => TeamMatches(resource, scopedTeam)));
    }

    public async Task<IReadOnlyList<BudgetDefinition>> GetBudgetsAsync(CancellationToken cancellationToken = default) =>
        await _db.Budgets.AsNoTracking().OrderBy(budget => budget.Id).ToListAsync(cancellationToken);

    public async Task<BudgetDefinition> CreateBudgetAsync(CreateBudgetRequest request, CancellationToken cancellationToken = default)
    {
        if (await _db.Budgets.AnyAsync(budget => budget.Id == request.Id, cancellationToken))
            throw new InvalidOperationException("A budget with this id already exists.");
        var budget = new BudgetDefinition(request.Id, request.Scope, request.Selector, request.Amount, request.PeriodStart, request.PeriodEnd, request.Cadence);
        _db.Budgets.Add(budget);
        await WriteAuditAsync("system", "budget.created", budget.Id, "{}", JsonSerializer.Serialize(request), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return budget;
    }

    public async Task<IReadOnlyList<BudgetStatus>> GetBudgetStatusesAsync(CancellationToken cancellationToken = default)
    {
        var budgets = await _db.Budgets.ToListAsync(cancellationToken);
        var resources = await _db.Resources.AsNoTracking().ToListAsync(cancellationToken);
        var costs = await _db.Costs.AsNoTracking().ToListAsync(cancellationToken);
        var statuses = new List<BudgetStatus>();
        foreach (var budget in budgets)
        {
            var matchingResourceIds = resources.Where(resource => BudgetService.AppliesTo(budget, resource)).Select(resource => resource.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var spend = costs
                .Where(cost => cost.UsageDate >= budget.PeriodStart && cost.UsageDate <= budget.PeriodEnd && matchingResourceIds.Contains(cost.ResourceId))
                .Sum(cost => cost.Cost(CostBasis.Amortized));
            statuses.Add(_budgetService.Evaluate(budget, spend));
        }
        await _db.SaveChangesAsync(cancellationToken);
        return statuses;
    }

    public async Task<IReadOnlyList<ForecastResult>> GetForecastsAsync(string? team, CancellationToken cancellationToken = default)
    {
        var resources = await _db.Resources.AsNoTracking().ToListAsync(cancellationToken);
        var allowedResourceIds = resources.Where(resource => TeamMatches(resource, team)).Select(resource => resource.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var costs = await _db.Costs.AsNoTracking().Where(cost => !cost.IsHourly).ToListAsync(cancellationToken);
        var series = costs
            .Where(cost => allowedResourceIds.Contains(cost.ResourceId))
            .GroupBy(cost => cost.UsageDate)
            .Select(group => new DailyCostPoint(group.Key, group.Sum(cost => cost.Cost(CostBasis.Amortized))))
            .OrderBy(point => point.Date)
            .ToList();
        if (series.Count == 0) return [];
        var latest = series[^1].Date;
        var monthStart = new DateOnly(latest.Year, latest.Month, 1);
        var observed = series.Where(point => point.Date >= monthStart && point.Date.Day <= Math.Min(14, latest.Day)).ToList();
        var history = series.Where(point => point.Date < monthStart).ToList();
        return observed.Count == 0 ? [] : _forecastingService.ForecastAll(monthStart, history, observed);
    }

    public async Task<IReadOnlyList<AnomalyEvent>> GetAnomaliesAsync(bool includeSuppressed, CancellationToken cancellationToken = default)
    {
        var anomalies = (await _db.Anomalies.AsNoTracking().ToListAsync(cancellationToken))
            .OrderByDescending(anomaly => anomaly.SeverityScore)
            .ThenByDescending(anomaly => anomaly.DetectedOn)
            .ToList();
        return includeSuppressed ? anomalies : anomalies.Where(anomaly => !anomaly.Suppressed).ToList();
    }

    public async Task<AnomalyEvent?> UpdateAnomalyAsync(string id, AnomalyActionRequest request, CancellationToken cancellationToken = default)
    {
        var anomaly = await _db.Anomalies.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (anomaly is null) return null;
        var before = JsonSerializer.Serialize(anomaly);
        switch (request.Action.ToLowerInvariant())
        {
            case "ack": anomaly.Acknowledge(); break;
            case "suppress": anomaly.Suppress(); break;
            default: throw new ArgumentException("Action must be ack or suppress.", nameof(request));
        }
        await WriteAuditAsync("system", $"anomaly.{request.Action}", id, before, JsonSerializer.Serialize(anomaly), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return anomaly;
    }

    public async Task<IReadOnlyList<Recommendation>> GetRecommendationsAsync(CancellationToken cancellationToken = default) =>
        (await _db.Recommendations.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(recommendation => recommendation.Lifecycle)
            .ThenByDescending(recommendation => recommendation.ProjectedMonthlySavings)
            .ToList();

    public async Task<Recommendation?> UpdateRecommendationAsync(string id, RecommendationActionRequest request, CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (recommendation is null) return null;
        var before = JsonSerializer.Serialize(recommendation);
        switch (request.Action.ToLowerInvariant())
        {
            case "accept": recommendation.Accept(); break;
            case "dismiss": recommendation.Dismiss(); break;
            case "implement" when request.BaselineMonthlyCost is not null: recommendation.Implement(request.BaselineMonthlyCost.Value); break;
            case "verify" when request.PostChangeMonthlyCost is not null: recommendation.Verify(request.PostChangeMonthlyCost.Value); break;
            case "implement": throw new ArgumentException("baselineMonthlyCost is required to implement.", nameof(request));
            case "verify": throw new ArgumentException("postChangeMonthlyCost is required to verify.", nameof(request));
            default: throw new ArgumentException("Action must be accept, dismiss, implement or verify.", nameof(request));
        }
        await WriteAuditAsync("system", $"recommendation.{request.Action}", id, before, JsonSerializer.Serialize(recommendation), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return recommendation;
    }

    public async Task<IReadOnlyList<UnitEconomicsPoint>> GetUnitEconomicsAsync(string? scopedTeam, CancellationToken cancellationToken = default)
    {
        var (from, to) = ResolveRange(null, null);
        var report = await GetAllocationsAsync(new AllocationQuery(from, to, scopedTeam, "amortized", 1, 10_000), scopedTeam, cancellationToken);
        var metrics = await _db.BusinessMetrics.AsNoTracking().Where(metric => metric.Date >= from && metric.Date <= to).ToListAsync(cancellationToken);
        return _unitEconomics.Calculate(report.Lines.Items, metrics.Select(metric => metric.ToDomain()).Where(metric => string.IsNullOrWhiteSpace(scopedTeam) || string.Equals(metric.Team, scopedTeam, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<IReadOnlyList<AnomalyGroup>> GetAnomalyGroupsAsync(CancellationToken cancellationToken = default) =>
        _anomalyDetection.GroupRelated(await GetAnomaliesAsync(true, cancellationToken));

    private async Task UpsertBatchAsync(List<CostRecord> batch, CancellationToken cancellationToken, Action<(int Inserted, int Restated)> recordCount, Action<(int Inserted, int Restated)> restatementCount)
    {
        var latestByKey = batch.GroupBy(cost => cost.IdempotencyKey, StringComparer.Ordinal).Select(group => group.Last()).ToList();
        batch.Clear();
        var keys = latestByKey.Select(cost => cost.IdempotencyKey).ToList();
        var existingByKey = (await _db.Costs.Where(cost => keys.Contains(cost.IdempotencyKey)).ToListAsync(cancellationToken))
            .ToDictionary(cost => cost.IdempotencyKey, StringComparer.Ordinal);
        var inserted = 0;
        var restated = 0;
        foreach (var record in latestByKey)
        {
            if (existingByKey.TryGetValue(record.IdempotencyKey, out var old))
            {
                old.RestateFrom(record);
                restated++;
            }
            else
            {
                _db.Costs.Add(record);
                inserted++;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
        var result = (inserted, restated);
        recordCount(result);
        restatementCount(result);
    }

    private async Task GenerateAnomaliesAsync(CancellationToken cancellationToken)
    {
        var anomalyResourceIds = _syntheticData.KnownInjectedAnomalies.Select(anomaly => anomaly.ResourceId).Distinct().ToList();
        var costs = await _db.Costs.AsNoTracking()
            .Where(cost => !cost.IsHourly)
            .ToListAsync(cancellationToken);
        var resources = await _db.Resources.AsNoTracking().ToListAsync(cancellationToken);
        var resourcesById = resources.ToDictionary(resource => resource.Id, StringComparer.OrdinalIgnoreCase);
        var points = new List<SeriesPoint>();
        points.AddRange(costs.GroupBy(cost => cost.UsageDate)
            .Select(group => new SeriesPoint(group.Key, "total", "Northstar Group", group.Sum(cost => cost.Cost(CostBasis.Amortized)))));
        points.AddRange(costs.GroupBy(cost => (cost.UsageDate, cost.Service))
            .Select(group => new SeriesPoint(group.Key.UsageDate, "service", group.Key.Service, group.Sum(cost => cost.Cost(CostBasis.Amortized)))));
        points.AddRange(costs
            .Where(cost => resourcesById.ContainsKey(cost.ResourceId))
            .GroupBy(cost => (cost.UsageDate, Team: _tagGovernance.Normalize(resourcesById[cost.ResourceId].Tags).TryGetValue("team", out var team) ? team : "unallocated"))
            .Select(group => new SeriesPoint(group.Key.UsageDate, "team", group.Key.Team, group.Sum(cost => cost.Cost(CostBasis.Amortized)))));
        points.AddRange(costs.Where(cost => anomalyResourceIds.Contains(cost.ResourceId))
            .Select(cost => new SeriesPoint(cost.UsageDate, "resource", cost.ResourceId, cost.Cost(CostBasis.Amortized))));
        var suppressions = new[] { new PlannedSuppression(SyntheticFinOpsData.DefaultStart, SyntheticFinOpsData.DefaultEnd, "res-002", "Known recurring month-end batch.") };
        var detected = _anomalyDetection.Detect(points, suppressions);
        var deduplicated = _anomalyDetection.DeduplicateRelated(detected);
        if (deduplicated.Count > 0)
        {
            _db.Anomalies.AddRange(deduplicated);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task GenerateRecommendationsAsync(CancellationToken cancellationToken)
    {
        var resources = await _db.Resources.AsNoTracking().ToListAsync(cancellationToken);
        var lastDate = await _db.Costs.AsNoTracking().OrderByDescending(cost => cost.UsageDate).Select(cost => cost.UsageDate).FirstAsync(cancellationToken);
        var costs = await _db.Costs.AsNoTracking().Where(cost => cost.UsageDate >= lastDate.AddDays(-30) && !cost.IsHourly).ToListAsync(cancellationToken);
        var signals = resources.Select(resource =>
        {
            var index = int.TryParse(resource.Id.AsSpan(4), out var parsed) ? parsed : 0;
            var monthlyCost = costs.Where(cost => cost.ResourceId == resource.Id).Sum(cost => cost.Cost(CostBasis.Amortized));
            return new OptimizationSignal(
                resource,
                monthlyCost,
                index % 5 == 0 ? 6m : 27m + index % 18,
                index % 5 == 0 ? 14m : 38m + index % 24,
                index % 5 == 0 ? 21 : 10,
                500m + index % 240,
                index % 7,
                index % 3 == 0 ? 78m : 45m,
                IsAttached: index % 47 != 0,
                IsUnusedPublicIp: resource.Category == ResourceCategory.Networking && index % 19 == 0,
                IsEmptyLoadBalancer: resource.Category == ResourceCategory.Networking && index % 23 == 0,
                IsStaleSnapshot: resource.Category == ResourceCategory.Storage && index % 29 == 0);
        });
        _db.Recommendations.AddRange(_recommendationEngine.Generate(signals));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<AllocationRule> DefaultRules() =>
    [
        new(10, AllocationMethod.DirectTag),
        new(20, AllocationMethod.ResourceGroupMapping, "resourceGroup", "data-development-rg", "data", "cc-300"),
        new(30, AllocationMethod.ParentInheritance),
        new(40, AllocationMethod.SharedProportional, "category", "Networking", SplitStrategy: SharedSplitStrategy.ProportionalUsage),
        new(50, AllocationMethod.SharedEven, "category", "Monitoring", SplitStrategy: SharedSplitStrategy.Even),
        new(60, AllocationMethod.SharedFixed, "resourceGroup", "shared-fixed-rg", SplitStrategy: SharedSplitStrategy.FixedPercentage, FixedPercentages: new Dictionary<string, decimal> { ["platform"] = 60m, ["finops"] = 40m })
    ];

    private static AllocationRuleRow ToRow(AllocationRule rule) => new()
    {
        Order = rule.Order,
        Method = rule.Method,
        MatchKey = rule.MatchKey,
        MatchValue = rule.MatchValue,
        TargetTeam = rule.TargetTeam,
        TargetCostCentre = rule.TargetCostCentre,
        SplitStrategy = rule.SplitStrategy,
        FixedPercentagesJson = JsonSerializer.Serialize(rule.FixedPercentages ?? new Dictionary<string, decimal>()),
        Enabled = rule.Enabled
    };

    private static AllocationRule ToDomain(AllocationRuleRow row) =>
        new(row.Order, row.Method, row.MatchKey, row.MatchValue, row.TargetTeam, row.TargetCostCentre, row.SplitStrategy,
            JsonSerializer.Deserialize<Dictionary<string, decimal>>(row.FixedPercentagesJson) ?? new Dictionary<string, decimal>(), row.Enabled);

    private (DateOnly From, DateOnly To) ResolveRange(DateOnly? from, DateOnly? to)
    {
        var end = to ?? _clock.Today;
        var start = from ?? end.AddDays(-31);
        if (start > end) throw new ArgumentException("From must be on or before To.");
        return (start, end);
    }

    private bool TeamMatches(CloudResource resource, string? team) =>
        string.IsNullOrWhiteSpace(team) ||
        (_tagGovernance.Normalize(resource.Tags).TryGetValue("team", out var resourceTeam) && string.Equals(resourceTeam, team, StringComparison.OrdinalIgnoreCase));

    private string GroupKey(CostRecord cost, CloudResource resource, string? groupBy) =>
        groupBy?.ToLowerInvariant() switch
        {
            "service" => cost.Service,
            "team" => _tagGovernance.Normalize(resource.Tags).TryGetValue("team", out var team) ? team : "unallocated",
            "environment" => _tagGovernance.Normalize(resource.Tags).TryGetValue("environment", out var environment) ? environment : "unallocated",
            "resource" => resource.Id,
            "subscription" => resource.SubscriptionId,
            _ => cost.UsageDate.ToString("yyyy-MM-dd")
        };

    private async Task<decimal> ReportingRateAsync(string? reportingCurrency, CancellationToken cancellationToken)
    {
        if (!string.Equals(reportingCurrency, "KES", StringComparison.OrdinalIgnoreCase)) return 1m;
        var rate = await _db.FxRates.AsNoTracking()
            .Where(rate => rate.FromCurrency == "USD" && rate.ToCurrency == "KES")
            .OrderByDescending(rate => rate.RateDate)
            .Select(rate => rate.Rate)
            .FirstOrDefaultAsync(cancellationToken);
        return rate > 0m ? rate : 1m;
    }

    private async Task WriteAuditAsync(string actor, string action, string resource, string before, string after, CancellationToken cancellationToken)
    {
        var correlationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{before}|{after}")));
        _db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = action,
            Resource = resource,
            CorrelationId = correlationId,
            BeforeAfterHash = hash,
            OccurredAt = _clock.UtcNow
        });
        await Task.CompletedTask;
    }

    private static decimal Convert(decimal value, decimal rate) => value * rate;
    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static int NormalizePage(int page) => Math.Max(1, page);
    private static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize, 1, 10_000);
    private static PageResult<T> Page<T>(IReadOnlyList<T> values, int page, int pageSize) => new(values.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, values.Count);
}
