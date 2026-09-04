using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Rules;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;
using FraudPipeline.Infrastructure.Time;
using FraudPipeline.UnitTests.Fakes;

namespace FraudPipeline.UnitTests;

public class ScoringServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Transaction Txn(string txnRef, decimal amount = 100m, string mcc = "5411", string country = "US")
        => new(
            id: Guid.NewGuid(),
            transactionRef: txnRef,
            cardId: "CARD1",
            customerId: "CUST1",
            deviceId: "DEV1",
            ipAddress: "192.0.2.1",
            merchantId: "MERCH1",
            mcc: mcc,
            amount: Money.Of(amount, country == "KE" ? "KES" : "USD"),
            type: TransactionType.CardNotPresent,
            location: GeoLocation.Of(country == "KE" ? -1.29 : 40.71, country == "KE" ? 36.82 : -74.00, country),
            occurredAt: _now,
            receivedAt: _now.AddSeconds(1));

    private static (ScoringService svc, InMemoryScoringDecisionRepository decisions, InMemoryTransactionRepository txnRepo, InMemoryListRepository lists, InMemoryRulesetRepository rulesetRepo, FeatureStoreService fs, FakeClock clock)
        BuildSvc(RulesetDefinition def, ScoringOptions? options = null, ListEntry[]? initialLists = null)
    {
        var clock = new FakeClock(_now);
        var ids = new GuidIdGenerator();
        var runtime = new FeatureStoreRuntime(clock);
        var fs = new FeatureStoreService(runtime);
        var engine = new RuleEngine();
        var metrics = new ScoringMetrics();
        var decisions = new InMemoryScoringDecisionRepository();
        var txnRepo = new InMemoryTransactionRepository();
        var lists = new InMemoryListRepository();
        if (initialLists is not null)
        {
            foreach (var e in initialLists) lists.AddAsync(e).GetAwaiter().GetResult();
        }
        var rulesetRepo = new InMemoryRulesetRepository();
        var r = new Ruleset(Guid.NewGuid(), def.Version, def.Name, RulesetSerializer.Serialize(def), _now);
        r.Activate(_now);
        rulesetRepo.AddAsync(r).GetAwaiter().GetResult();
        var svc = new ScoringService(fs, engine, rulesetRepo, lists, txnRepo, decisions, ids, clock, options ?? new ScoringOptions { EnableShadow = false }, metrics);
        return (svc, decisions, txnRepo, lists, rulesetRepo, fs, clock);
    }

    [Fact]
    public async Task Score_NoRulesFire_ReturnsApproveWithZero()
    {
        var def = new RulesetDefinition("v-empty", "empty", Array.Empty<RuleDefinition>(), new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());
        var (svc, _, _, _, _, _, _) = BuildSvc(def);
        var res = await svc.ScoreAsync(Txn("TX-01"));
        Assert.Equal(0, res.Score);
        Assert.Equal(Decision.Approve, res.Decision);
    }

    [Fact]
    public async Task Score_AboveDeclineBand_ReturnsDecline()
    {
        var def = new RulesetDefinition("v-mcc", "high-risk", new[]
        {
            new RuleDefinition("mcc", RuleKind.MerchantMccRisk, 900, new Dictionary<string, string>())
        }, new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());
        var (svc, _, _, _, _, _, _) = BuildSvc(def);
        var res = await svc.ScoreAsync(Txn("TX-02", mcc: "6051"));
        Assert.Equal(Decision.Decline, res.Decision);
        Assert.True(res.Score >= 800);
    }

    [Fact]
    public async Task Score_AllowListPresent_OverridesAllOtherRules()
    {
        var def = new RulesetDefinition("v-allow", "allow-override", new[]
        {
            new RuleDefinition("mcc", RuleKind.MerchantMccRisk, 900, new Dictionary<string, string>()),
            new RuleDefinition("al", RuleKind.AllowList, 0, new Dictionary<string, string>())
        }, new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());
        var lists = new[] { new ListEntry(Guid.NewGuid(), ListType.Allow, ListSubject.Card, "CARD1", "trusted", _now) };
        var (svc, _, _, _, _, _, _) = BuildSvc(def, initialLists: lists);
        var res = await svc.ScoreAsync(Txn("TX-03", mcc: "6051"));
        Assert.Equal(Decision.Approve, res.Decision);
        Assert.Equal(0, res.Score);
    }

    [Fact]
    public async Task Score_DenyListPresent_ForcesDecline()
    {
        var def = new RulesetDefinition("v-deny", "deny-forces", new[]
        {
            new RuleDefinition("dl", RuleKind.DenyList, 1000, new Dictionary<string, string>())
        }, new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());
        var lists = new[] { new ListEntry(Guid.NewGuid(), ListType.Deny, ListSubject.Merchant, "MERCH1", "known bad", _now) };
        var (svc, _, _, _, _, _, _) = BuildSvc(def, initialLists: lists);
        var res = await svc.ScoreAsync(Txn("TX-04"));
        Assert.Equal(Decision.Decline, res.Decision);
    }

    [Fact]
    public async Task Score_SameTxnSameRuleset_ProducesSameScore()
    {
        var def = new RulesetDefinition("v-repro", "reproducibility", new[]
        {
            new RuleDefinition("mcc", RuleKind.MerchantMccRisk, 300, new Dictionary<string, string>())
        }, new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());
        var (svc, _, _, _, _, _, _) = BuildSvc(def);
        var t1 = Txn("TX-05", mcc: "6051");
        var t2 = Txn("TX-06", mcc: "6051");
        var r1 = await svc.ScoreAsync(t1);
        var r2 = await svc.ScoreAsync(t2);
        Assert.Equal(r1.Score, r2.Score);
        Assert.Equal(r1.Decision, r2.Decision);
        Assert.Equal(r1.RulesetVersion, r2.RulesetVersion);
    }

    [Fact]
    public async Task Score_ExceedsLatencyBudget_DegradesToReview()
    {
        // Budget of 0 ms guarantees the budget is always exceeded.
        var def = new RulesetDefinition("v-budget", "budget", Array.Empty<RuleDefinition>(), new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());
        var opts = new ScoringOptions { LatencyBudgetMs = 0, DegradedDecision = Decision.Review, EnableShadow = false };
        var (svc, _, _, _, _, _, _) = BuildSvc(def, opts);
        var res = await svc.ScoreAsync(Txn("TX-07"));
        Assert.True(res.BudgetExceeded);
        Assert.True(res.Decision >= Decision.Review);
        Assert.Contains("budget_exceeded", res.Reasons);
    }

    [Fact]
    public void RiskBands_ClassifyBoundaries()
    {
        var bands = new RiskBands(200, 500, 800);
        Assert.Equal(Decision.Approve, bands.Classify(199));
        Assert.Equal(Decision.StepUp, bands.Classify(200));
        Assert.Equal(Decision.StepUp, bands.Classify(499));
        Assert.Equal(Decision.Review, bands.Classify(500));
        Assert.Equal(Decision.Review, bands.Classify(799));
        Assert.Equal(Decision.Decline, bands.Classify(800));
    }

    [Fact]
    public async Task Score_MerchantPolicyOverride_ForcesReview()
    {
        var overrides = new Dictionary<string, MerchantPolicy>
        {
            ["MERCH1"] = new MerchantPolicy("MERCH1", ScoreOffset: null, OverrideDecision: null, ForceReview: true)
        };
        var def = new RulesetDefinition("v-mp", "merchant-policy",
            Array.Empty<RuleDefinition>(),
            new RiskBands(200, 500, 800),
            overrides);
        var (svc, _, _, _, _, _, _) = BuildSvc(def);
        var res = await svc.ScoreAsync(Txn("TX-08"));
        Assert.Equal(Decision.Review, res.Decision);
    }
}
