using FraudPipeline.Application.Feedback;
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

public class DetectionEvaluatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Transaction Txn(string txnRef, decimal amount, bool fraud, string? pattern = null)
        => new(
            id: Guid.NewGuid(),
            transactionRef: txnRef,
            cardId: "CARD1",
            customerId: "CUST1",
            deviceId: "DEV1",
            ipAddress: "192.0.2.1",
            merchantId: "MERCH1",
            mcc: "5411",
            amount: Money.Of(amount, "USD"),
            type: TransactionType.CardNotPresent,
            location: GeoLocation.Of(40.71, -74.00, "US"),
            occurredAt: _now,
            receivedAt: _now.AddSeconds(1),
            groundTruthFraud: fraud,
            groundTruthPattern: pattern);

    private static ScoringDecision Dec(Transaction t, Decision decision)
        => new(
            id: Guid.NewGuid(),
            transactionId: t.Id,
            transactionRef: t.TransactionRef,
            rulesetVersion: "v-test",
            score: decision == Decision.Approve ? 10 : 700,
            decision: decision,
            reasons: "",
            rulesFiredJson: "[]",
            featureVectorJson: "{}",
            latencyMs: 1.0,
            budgetExceeded: false,
            shadow: false,
            decidedAt: _now);

    [Fact]
    public async Task ComputeAsync_PerfectClassifier_YieldsPrecisionAndRecallOne()
    {
        var clock = new FakeClock(_now);
        var txns = new InMemoryTransactionRepository();
        var decisions = new InMemoryScoringDecisionRepository();
        var lists = new InMemoryListRepository();

        // 3 fraud + 3 non-fraud, all classified correctly.
        var frauds = new[] { Txn("TX-F1", 100, true), Txn("TX-F2", 200, true), Txn("TX-F3", 300, true) };
        var goods = new[] { Txn("TX-G1", 10, false), Txn("TX-G2", 20, false), Txn("TX-G3", 30, false) };
        foreach (var t in frauds.Concat(goods)) await txns.AddAsync(t);
        foreach (var t in frauds) await decisions.AddAsync(Dec(t, Decision.Decline));
        foreach (var t in goods) await decisions.AddAsync(Dec(t, Decision.Approve));

        // Build scoring service just to construct DetectionEvaluator (never used by ComputeAsync).
        var runtime = new FeatureStoreRuntime(clock);
        var fs = new FeatureStoreService(runtime);
        var engine = new RuleEngine();
        var metrics = new ScoringMetrics();
        var rsRepo = new InMemoryRulesetRepository();
        var scoring = new ScoringService(fs, engine, rsRepo, lists, txns, decisions, new GuidIdGenerator(), clock, new ScoringOptions { EnableShadow = false }, metrics);
        var eval = new DetectionEvaluator(decisions, txns, fs, scoring, lists);

        var m = await eval.ComputeAsync(100);
        Assert.Equal(6, m.LabelledTransactions);
        Assert.Equal(3, m.TruePositives);
        Assert.Equal(0, m.FalsePositives);
        Assert.Equal(3, m.TrueNegatives);
        Assert.Equal(0, m.FalseNegatives);
        Assert.Equal(1.0, m.Precision);
        Assert.Equal(1.0, m.Recall);
        Assert.Equal(0.0, m.FalsePositiveRate);
        Assert.Equal(600m, m.ValueDetected);
    }

    [Fact]
    public async Task ComputeAsync_MixedOutcomes_ComputesConfusionMatrix()
    {
        var clock = new FakeClock(_now);
        var txns = new InMemoryTransactionRepository();
        var decisions = new InMemoryScoringDecisionRepository();
        var lists = new InMemoryListRepository();

        // 4 fraud (3 caught, 1 missed) + 4 non-fraud (1 flagged, 3 approved)
        var t1 = Txn("TX-1", 100, true); await txns.AddAsync(t1); await decisions.AddAsync(Dec(t1, Decision.Decline));
        var t2 = Txn("TX-2", 100, true); await txns.AddAsync(t2); await decisions.AddAsync(Dec(t2, Decision.Review));
        var t3 = Txn("TX-3", 100, true); await txns.AddAsync(t3); await decisions.AddAsync(Dec(t3, Decision.Decline));
        var t4 = Txn("TX-4", 100, true); await txns.AddAsync(t4); await decisions.AddAsync(Dec(t4, Decision.Approve)); // missed
        var t5 = Txn("TX-5", 100, false); await txns.AddAsync(t5); await decisions.AddAsync(Dec(t5, Decision.Approve));
        var t6 = Txn("TX-6", 100, false); await txns.AddAsync(t6); await decisions.AddAsync(Dec(t6, Decision.Review)); // FP
        var t7 = Txn("TX-7", 100, false); await txns.AddAsync(t7); await decisions.AddAsync(Dec(t7, Decision.Approve));
        var t8 = Txn("TX-8", 100, false); await txns.AddAsync(t8); await decisions.AddAsync(Dec(t8, Decision.Approve));

        var runtime = new FeatureStoreRuntime(clock);
        var fs = new FeatureStoreService(runtime);
        var engine = new RuleEngine();
        var metrics = new ScoringMetrics();
        var rsRepo = new InMemoryRulesetRepository();
        var scoring = new ScoringService(fs, engine, rsRepo, lists, txns, decisions, new GuidIdGenerator(), clock, new ScoringOptions { EnableShadow = false }, metrics);
        var eval = new DetectionEvaluator(decisions, txns, fs, scoring, lists);
        var m = await eval.ComputeAsync(100);

        Assert.Equal(3, m.TruePositives);
        Assert.Equal(1, m.FalsePositives);
        Assert.Equal(3, m.TrueNegatives);
        Assert.Equal(1, m.FalseNegatives);
        Assert.Equal(0.75, m.Precision, 3);
        Assert.Equal(0.75, m.Recall, 3);
        Assert.Equal(0.25, m.FalsePositiveRate, 3);
    }
}
