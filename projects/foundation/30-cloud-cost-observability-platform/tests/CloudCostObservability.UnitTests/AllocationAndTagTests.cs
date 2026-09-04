using CloudCostObservability.Domain.Models;
using CloudCostObservability.Domain.Services;

namespace CloudCostObservability.UnitTests;

public sealed class AllocationAndTagTests
{
    private readonly AllocationEngine _engine = new();

    [Fact]
    public void Allocate_DirectTag_AttributesFullAmountToTaggedTeam()
    {
        var audit = _engine.Allocate([TestData.Cost()], [TestData.Resource()], [new AllocationRule(10, AllocationMethod.DirectTag)]).Single();
        var line = Assert.Single(audit.Lines);
        Assert.Equal("commerce", line.Team);
        Assert.Equal(100m, line.Amount);
        Assert.Equal(AllocationMethod.DirectTag, line.Method);
    }

    [Fact]
    public void Allocate_ResourceGroupMapping_UsesMappedTeamWhenTagsMissing()
    {
        var resource = TestData.Resource(tags: new Dictionary<string, string> { ["owner"] = "nobody" });
        var audit = _engine.Allocate([TestData.Cost()], [resource], [new AllocationRule(10, AllocationMethod.ResourceGroupMapping, "resourceGroup", "commerce-production-rg", "atlas", "cc-100")]).Single();
        var line = Assert.Single(audit.Lines);
        Assert.Equal("atlas", line.Team);
        Assert.Equal("cc-100", line.CostCentre);
    }

    [Fact]
    public void Allocate_ParentInheritance_UsesParentTagsForChildCost()
    {
        var parent = TestData.Resource("vm-1", "data");
        var child = TestData.Resource("disk-1", tags: new Dictionary<string, string>(), parent: "vm-1", category: ResourceCategory.Storage);
        var audit = _engine.Allocate([TestData.Cost("disk-1")], [parent, child], [new AllocationRule(10, AllocationMethod.ParentInheritance)]).Single();
        var line = Assert.Single(audit.Lines);
        Assert.Equal("data", line.Team);
        Assert.Equal(AllocationMethod.ParentInheritance, line.Method);
    }

    [Fact]
    public void Allocate_SharedProportional_SplitsByProvidedUsageWeights()
    {
        var shared = TestData.Resource("network", category: ResourceCategory.Networking, tags: new Dictionary<string, string>());
        var rule = new AllocationRule(10, AllocationMethod.SharedProportional, "category", "Networking");
        var audit = _engine.Allocate([TestData.Cost("network", 90m, 90m)], [shared], [rule], teamUsageWeights: new Dictionary<string, decimal> { ["atlas"] = 1m, ["commerce"] = 2m }).Single();
        Assert.Equal(30m, audit.Lines.Single(x => x.Team == "atlas").Amount);
        Assert.Equal(60m, audit.Lines.Single(x => x.Team == "commerce").Amount);
        Assert.Equal(90m, audit.AllocatedAmount);
    }

    [Fact]
    public void Allocate_SharedEven_SplitsFullAmountAcrossTeams()
    {
        var shared = TestData.Resource("monitoring", category: ResourceCategory.Monitoring, tags: new Dictionary<string, string>());
        var rule = new AllocationRule(10, AllocationMethod.SharedEven, "category", "Monitoring");
        var audit = _engine.Allocate([TestData.Cost("monitoring", 100m)], [shared], [rule], teamUsageWeights: new Dictionary<string, decimal> { ["atlas"] = 1m, ["commerce"] = 1m, ["data"] = 1m }).Single();
        Assert.Equal(100m, audit.Lines.Sum(x => x.Amount));
        Assert.Equal(3, audit.Lines.Count);
        Assert.Equal(0m, audit.UnallocatedAmount);
    }

    [Fact]
    public void Allocate_SharedFixed_UsesExactFixedPercentages()
    {
        var shared = TestData.Resource("shared", category: ResourceCategory.Networking, tags: new Dictionary<string, string>());
        var rule = new AllocationRule(10, AllocationMethod.SharedFixed, "category", "Networking", FixedPercentages: new Dictionary<string, decimal> { ["atlas"] = 60m, ["commerce"] = 40m });
        var audit = _engine.Allocate([TestData.Cost("shared", 250m, 250m)], [shared], [rule]).Single();
        Assert.Equal(150m, audit.Lines.Single(x => x.Team == "atlas").Amount);
        Assert.Equal(100m, audit.Lines.Single(x => x.Team == "commerce").Amount);
    }

    [Fact]
    public void Allocate_PartialFixedSchedule_RetainsExplicitUnallocatedResidual()
    {
        var shared = TestData.Resource("shared", category: ResourceCategory.Networking, tags: new Dictionary<string, string>());
        var rule = new AllocationRule(10, AllocationMethod.SharedFixed, "category", "Networking", FixedPercentages: new Dictionary<string, decimal> { ["atlas"] = 60m });
        var audit = _engine.Allocate([TestData.Cost("shared", 100m)], [shared], [rule]).Single();
        Assert.Equal(60m, audit.AllocatedAmount);
        Assert.Equal(40m, audit.UnallocatedAmount);
        Assert.Equal("unallocated", audit.Lines.Last().Team);
    }

    [Fact]
    public void Allocate_ManyPeriodsAndRuleSets_AlwaysReconcilesExactly()
    {
        var resources = Enumerable.Range(0, 30).Select(index =>
            TestData.Resource($"r-{index}", index % 4 == 0 ? "atlas" : "commerce",
                index % 7 == 0 ? ResourceCategory.Networking : ResourceCategory.Compute,
                tags: index % 4 == 0 ? new Dictionary<string, string>() : null)).ToList();
        var costs = Enumerable.Range(0, 180).Select(index =>
            TestData.Cost($"r-{index % resources.Count}", 10m + index / 10m, date: new DateOnly(2026, 1, 1).AddDays(index))).ToList();
        var rules = new[]
        {
            new AllocationRule(10, AllocationMethod.DirectTag),
            new AllocationRule(20, AllocationMethod.SharedProportional, "category", "Networking")
        };
        var audits = _engine.Allocate(costs, resources, rules, teamUsageWeights: new Dictionary<string, decimal> { ["atlas"] = 3m, ["commerce"] = 7m });
        Assert.All(audits, AllocationEngine.AssertInvariant);
        Assert.Equal(costs.Sum(x => x.AmortizedCost), audits.Sum(x => x.Lines.Sum(line => line.Amount)));
    }

    [Fact]
    public void Allocate_OrderedRules_PrefersDirectTagBeforeResourceGroupMapping()
    {
        var resource = TestData.Resource(team: "commerce");
        var audits = _engine.Allocate([TestData.Cost()], [resource],
        [
            new AllocationRule(10, AllocationMethod.DirectTag),
            new AllocationRule(20, AllocationMethod.ResourceGroupMapping, "resourceGroup", "commerce-production-rg", "atlas", "cc-100")
        ]);
        Assert.Equal("commerce", audits.Single().Lines.Single().Team);
    }

    [Fact]
    public void Allocate_DirectTag_NormalizesCanonicalTeamTyposBeforeAttribution()
    {
        var resource = TestData.Resource(team: "platfrom");
        var audit = _engine.Allocate([TestData.Cost()], [resource], [new AllocationRule(10, AllocationMethod.DirectTag)]).Single();
        Assert.Equal("platform", audit.Lines.Single().Team);
    }

    [Fact]
    public void RollUpCostsToParents_AssignsChildSpendToRootResource()
    {
        var parent = TestData.Resource("vm");
        var child = TestData.Resource("disk", parent: "vm", category: ResourceCategory.Storage);
        var rollup = _engine.RollUpCostsToParents([TestData.Cost("vm", 30m, 30m), TestData.Cost("disk", 12m, 12m)], [parent, child]);
        Assert.Equal(42m, rollup["vm"]);
        Assert.DoesNotContain("disk", rollup.Keys);
    }

    [Fact]
    public void AllocationAudit_ContainsRuleExplanationForSpecificCharge()
    {
        var audit = _engine.Allocate([TestData.Cost()], [TestData.Resource()], [new AllocationRule(42, AllocationMethod.DirectTag)]).Single();
        var line = Assert.Single(audit.Lines);
        Assert.Equal("rule-42", line.RuleReference);
        Assert.Contains("Direct tag", line.Explanation);
    }

    [Fact]
    public void Money_AddSameCurrency_ReturnsSum()
    {
        var sum = new Money(5.25m, "usd") + new Money(4.75m, "USD");
        Assert.Equal(10m, sum.Amount);
        Assert.Equal("USD", sum.Currency);
    }

    [Fact]
    public void Money_AddDifferentCurrency_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _ = new Money(1m, "USD") + new Money(1m, "KES"));
    }

    [Fact]
    public void FxRate_ConvertsUsdToKesUsingConfiguredRate()
    {
        var converted = new FxRate(new DateOnly(2026, 1, 1), "USD", "KES", 130.5m).Convert(new Money(10m, "USD"));
        Assert.Equal(1305m, converted.Amount);
        Assert.Equal("KES", converted.Currency);
    }

    [Fact]
    public void CostRecord_RestateFrom_ReplacesAmountsWithoutChangingIdentity()
    {
        var original = TestData.Cost(actual: 10m, amortized: 10m);
        var restated = TestData.Cost(actual: 15m, amortized: 14m);
        original.RestateFrom(restated);
        Assert.Equal(14m, original.AmortizedCost);
        Assert.Equal(TestData.Cost().IdempotencyKey, original.IdempotencyKey);
    }

    [Fact]
    public void TagNormalization_NormalizesSynonymsAndCanonicalTypo()
    {
        var service = new TagGovernanceService();
        var tags = service.Normalize(new Dictionary<string, string> { ["Environment"] = "PRD", ["TEAM"] = "platfrom" });
        Assert.Equal("production", tags["environment"]);
        Assert.Equal("platform", tags["team"]);
    }

    [Fact]
    public void TagCoverage_ReportsMissingTagsAndRemediationWorklist()
    {
        var resource = TestData.Resource(tags: new Dictionary<string, string> { ["team"] = "commerce" });
        var report = new TagGovernanceService().BuildCoverage([resource]);
        Assert.Equal(0, report.FullyCompliantResources);
        Assert.Equal(4, report.MissingByTag.Count);
        Assert.Contains(report.Worklist, item => item.MissingTag == "owner" && item.ResourceId == "r1");
    }

    [Fact]
    public void TagCoverage_AllRequiredTags_ReturnsFullCoverage()
    {
        var report = new TagGovernanceService().BuildCoverage([TestData.Resource()]);
        Assert.Equal(100m, report.CoveragePercent);
        Assert.Empty(report.Worklist);
    }

    [Fact]
    public void Budget_Evaluate_CrossesThresholdAndSuppressesRepeatedAlert()
    {
        var budget = new BudgetDefinition("b1", BudgetScope.Team, "commerce", 100m, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var service = new BudgetService();
        var first = service.Evaluate(budget, 85m);
        var repeat = service.Evaluate(budget, 90m);
        Assert.Equal(new[] { 50, 80 }, first.NewlyCrossedThresholds);
        Assert.Empty(repeat.NewlyCrossedThresholds);
    }

    [Fact]
    public void Budget_Evaluate_CrossesHundredAndOneTwentyThresholds()
    {
        var budget = new BudgetDefinition("b1", BudgetScope.Team, "commerce", 100m, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var status = new BudgetService().Evaluate(budget, 125m);
        Assert.Equal(new[] { 50, 80, 100, 120 }, status.NewlyCrossedThresholds);
        Assert.Equal(125m, status.PercentUsed);
    }

    [Fact]
    public void Budget_AppliesToTeam_UsesTeamTag()
    {
        var budget = new BudgetDefinition("b1", BudgetScope.Team, "commerce", 100m, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Assert.True(BudgetService.AppliesTo(budget, TestData.Resource(team: "commerce")));
        Assert.False(BudgetService.AppliesTo(budget, TestData.Resource(team: "atlas")));
    }
}
