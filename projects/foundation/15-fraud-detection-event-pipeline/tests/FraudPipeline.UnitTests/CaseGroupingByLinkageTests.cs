using FraudPipeline.Application.Cases;
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

public class CaseGroupingByLinkageTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Transaction Txn(string cardId, string txnRef, decimal amount = 500m)
        => new(
            id: Guid.NewGuid(),
            transactionRef: txnRef,
            cardId: cardId,
            customerId: "CUST-" + cardId,
            deviceId: "DEV1",
            ipAddress: "192.0.2.1",
            merchantId: "MERCH1",
            mcc: "6051", // high-risk MCC
            amount: Money.Of(amount, "USD"),
            type: TransactionType.CardNotPresent,
            location: GeoLocation.Of(40.71, -74.00, "US"),
            occurredAt: _now,
            receivedAt: _now.AddSeconds(1));

    [Fact]
    public async Task TwoAlertsSameCard_GroupedIntoOneCase()
    {
        var clock = new FakeClock(_now);
        var ids = new GuidIdGenerator();
        var runtime = new FeatureStoreRuntime(clock);
        var fs = new FeatureStoreService(runtime);
        var engine = new RuleEngine();
        var metrics = new ScoringMetrics();
        var txnRepo = new InMemoryTransactionRepository();
        var decisions = new InMemoryScoringDecisionRepository();
        var listRepo = new InMemoryListRepository();
        var alerts = new InMemoryAlertRepository();
        var cases = new InMemoryCaseRepository();
        var rsRepo = new InMemoryRulesetRepository();

        var def = new RulesetDefinition("v-high", "test", new[]
        {
            new RuleDefinition("mcc", RuleKind.MerchantMccRisk, 900, new Dictionary<string, string>())
        }, new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());
        var r = new Ruleset(Guid.NewGuid(), def.Version, def.Name, RulesetSerializer.Serialize(def), _now);
        r.Activate(_now);
        await rsRepo.AddAsync(r);

        var scoring = new ScoringService(fs, engine, rsRepo, listRepo, txnRepo, decisions, ids, clock, new ScoringOptions { EnableShadow = false }, metrics);
        var caseSvc = new CaseManagementService(alerts, cases, ids, clock, new CaseManagementOptions { AlertThresholdScore = 500 });

        var t1 = Txn("CARD1", "TX-01");
        var t2 = Txn("CARD1", "TX-02");
        await txnRepo.AddAsync(t1);
        await txnRepo.AddAsync(t2);
        var r1 = await scoring.ScoreAsync(t1);
        var r2 = await scoring.ScoreAsync(t2);
        await caseSvc.HandleAsync(t1, r1, CancellationToken.None);
        await caseSvc.HandleAsync(t2, r2, CancellationToken.None);

        Assert.Equal(2, alerts.Items.Count);
        Assert.Single(cases.Items);
        Assert.Equal(2, cases.Items[0].AlertIds.Count);
    }
}
