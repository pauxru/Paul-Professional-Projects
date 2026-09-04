using CloudCostObservability.Domain.Models;
using CloudCostObservability.Domain.Services;

namespace CloudCostObservability.UnitTests;

public sealed class RecommendationAndEconomicsTests
{
    private readonly RecommendationEngine _engine = new();

    [Fact]
    public void Recommendations_IdleCompute_ProducesHandComputableFiftyPercentSavings()
    {
        var recommendation = _engine.Generate([Signal(TestData.Resource(category: ResourceCategory.Compute), 200m, cpu: 5m, memory: 10m, days: 21)]).Single(x => x.Type == RecommendationType.IdleCompute);
        Assert.Equal(100m, recommendation.ProjectedMonthlySavings);
        Assert.Equal(ConfidenceLevel.High, recommendation.Confidence);
    }

    [Fact]
    public void Recommendations_OrphanedDisk_ProducesFullMonthlyCostSavings()
    {
        var resource = TestData.Resource(category: ResourceCategory.Storage);
        var recommendation = _engine.Generate([Signal(resource, 75m, attached: false)]).Single(x => x.Type == RecommendationType.OrphanedResource);
        Assert.Equal(75m, recommendation.ProjectedMonthlySavings);
    }

    [Fact]
    public void Recommendations_OversizedSku_ProducesThirtyFivePercentSavings()
    {
        var recommendation = _engine.Generate([Signal(TestData.Resource(category: ResourceCategory.Compute), 200m, cpu: 30m, memory: 40m, days: 8)]).Single(x => x.Type == RecommendationType.OversizedSku);
        Assert.Equal(70m, recommendation.ProjectedMonthlySavings);
    }

    [Fact]
    public void Recommendations_NonProductionSchedule_CalculatesHoursSaved()
    {
        var resource = TestData.Resource(tags: Tags("development"));
        var recommendation = _engine.Generate([Signal(resource, 730m, runningHours: 730m)]).Single(x => x.Type == RecommendationType.NonProductionSchedule);
        Assert.Equal(510m, recommendation.ProjectedMonthlySavings);
        Assert.Contains("510", recommendation.Evidence);
    }

    [Fact]
    public void Recommendations_StorageTiering_ProducesFortyPercentSavings()
    {
        var recommendation = _engine.Generate([Signal(TestData.Resource(category: ResourceCategory.Storage), 150m, accesses: 1)]).Single(x => x.Type == RecommendationType.StorageTiering);
        Assert.Equal(60m, recommendation.ProjectedMonthlySavings);
    }

    [Fact]
    public void Recommendations_CommitmentPurchase_ProducesBreakEvenEvidence()
    {
        var recommendation = _engine.Generate([Signal(TestData.Resource(category: ResourceCategory.Compute), 500m, stable: 80m)]).Single(x => x.Type == RecommendationType.CommitmentPurchase);
        Assert.Equal(140m, recommendation.ProjectedMonthlySavings);
        Assert.Contains("breaks even", recommendation.Evidence);
    }

    [Fact]
    public void Recommendations_UntaggedResource_ProducesRemediationWorkItem()
    {
        var resource = TestData.Resource(tags: new Dictionary<string, string> { ["team"] = "commerce" });
        var recommendation = _engine.Generate([Signal(resource, 50m)]).Single(x => x.Type == RecommendationType.UntaggedResource);
        Assert.Equal(0m, recommendation.ProjectedMonthlySavings);
        Assert.Contains("Assign owner", recommendation.RemediationSteps);
    }

    [Fact]
    public void RecommendationLifecycle_AcceptImplementVerify_RecordsRealisedSavings()
    {
        var recommendation = new Recommendation("r1", RecommendationType.IdleCompute, "vm1", "idle", 80m, ConfidenceLevel.High, "evidence", "steps");
        recommendation.Accept();
        recommendation.Implement(200m);
        recommendation.Verify(125m);
        Assert.Equal(RecommendationLifecycle.Verified, recommendation.Lifecycle);
        Assert.Equal(75m, recommendation.RealisedMonthlySavings);
    }

    [Fact]
    public void RecommendationLifecycle_DismissAfterImplementation_IsRejected()
    {
        var recommendation = new Recommendation("r1", RecommendationType.IdleCompute, "vm1", "idle", 80m, ConfidenceLevel.High, "evidence", "steps");
        recommendation.Accept();
        recommendation.Implement(200m);
        Assert.Throws<InvalidOperationException>(() => recommendation.Dismiss());
    }

    [Fact]
    public void RecommendationLifecycle_VerifyBeforeImplementation_IsRejected()
    {
        var recommendation = new Recommendation("r1", RecommendationType.IdleCompute, "vm1", "idle", 80m, ConfidenceLevel.High, "evidence", "steps");
        Assert.Throws<InvalidOperationException>(() => recommendation.Verify(10m));
    }

    [Fact]
    public void UnitEconomics_JoinsAllocationAndBusinessMetricsByTeamAndDate()
    {
        var date = new DateOnly(2026, 1, 1);
        var allocation = new AllocationLine("c1", date, "commerce", "cc-200", 120m, AllocationMethod.DirectTag, "tag");
        var metric = new BusinessMetric(date, "commerce", 20, 4, 30m);
        var point = Assert.Single(new UnitEconomicsService().Calculate([allocation], [metric]));
        Assert.Equal(6m, point.CostPerOrder);
        Assert.Equal(30m, point.CostPerActiveTenant);
        Assert.Equal(4m, point.CostPerGigabyte);
    }

    [Fact]
    public void UnitEconomics_ZeroBusinessMetric_DoesNotDivideByZero()
    {
        var date = new DateOnly(2026, 1, 1);
        var allocation = new AllocationLine("c1", date, "commerce", "cc-200", 120m, AllocationMethod.DirectTag, "tag");
        var metric = new BusinessMetric(date, "commerce", 0, 0, 0m);
        var point = Assert.Single(new UnitEconomicsService().Calculate([allocation], [metric]));
        Assert.Equal(0m, point.CostPerOrder);
        Assert.Equal(0m, point.CostPerActiveTenant);
        Assert.Equal(0m, point.CostPerGigabyte);
    }

    [Fact]
    public void UnitEconomics_UnallocatedCost_IsExcludedFromTeamUnitCosts()
    {
        var date = new DateOnly(2026, 1, 1);
        var allocations = new[]
        {
            new AllocationLine("c1", date, "commerce", "cc-200", 100m, AllocationMethod.DirectTag, "tag"),
            new AllocationLine("c2", date, "unallocated", "unallocated", 200m, AllocationMethod.Unallocated, "residual")
        };
        var point = Assert.Single(new UnitEconomicsService().Calculate(allocations, [new BusinessMetric(date, "commerce", 10, 2, 10m)]));
        Assert.Equal(10m, point.CostPerOrder);
    }

    private static OptimizationSignal Signal(
        CloudResource resource,
        decimal monthlyCost,
        decimal cpu = 45m,
        decimal memory = 60m,
        int days = 0,
        decimal runningHours = 300m,
        int accesses = 10,
        decimal stable = 0m,
        bool attached = true) =>
        new(resource, monthlyCost, cpu, memory, days, runningHours, accesses, stable, attached);

    private static IReadOnlyDictionary<string, string> Tags(string environment) => new Dictionary<string, string>
    {
        ["owner"] = "owner",
        ["team"] = "commerce",
        ["environment"] = environment,
        ["cost-centre"] = "cc-200",
        ["application"] = "order-hub"
    };
}

