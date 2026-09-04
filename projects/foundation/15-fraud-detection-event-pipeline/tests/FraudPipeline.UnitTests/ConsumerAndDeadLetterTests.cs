using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Ingestion;
using FraudPipeline.Application.Rules;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;
using FraudPipeline.Infrastructure.Time;
using FraudPipeline.UnitTests.Fakes;

namespace FraudPipeline.UnitTests;

public class ConsumerAndDeadLetterTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Transaction Txn(string txnRef)
        => new(
            id: Guid.NewGuid(),
            transactionRef: txnRef,
            cardId: "CARD1",
            customerId: "CUST1",
            deviceId: "D1",
            ipAddress: "192.0.2.1",
            merchantId: "M1",
            mcc: "5411",
            amount: Money.Of(100m, "USD"),
            type: TransactionType.CardNotPresent,
            location: GeoLocation.Of(40.71, -74.00, "US"),
            occurredAt: _now,
            receivedAt: _now.AddSeconds(1));

    [Fact]
    public async Task Consumer_OnRepositoryFailure_WritesToDeadLetter()
    {
        var clock = new FakeClock(_now);
        var ids = new GuidIdGenerator();
        var bus = new PartitionedTransactionBus(new IngestionOptions { PartitionCount = 1 });
        var runtime = new FeatureStoreRuntime(clock);
        var fs = new FeatureStoreService(runtime);
        var engine = new RuleEngine();
        var metrics = new ScoringMetrics();
        var throwingRepo = new ThrowingTransactionRepository();
        var decisions = new InMemoryScoringDecisionRepository();
        var listRepo = new InMemoryListRepository();
        var rsRepo = new InMemoryRulesetRepository();
        var deadLetter = new InMemoryDeadLetterRepository();

        var def = new RulesetDefinition("v-empty", "empty", Array.Empty<RuleDefinition>(), new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());
        var r = new Ruleset(Guid.NewGuid(), def.Version, def.Name, RulesetSerializer.Serialize(def), _now);
        r.Activate(_now);
        await rsRepo.AddAsync(r);

        var scoring = new ScoringService(fs, engine, rsRepo, listRepo, throwingRepo, decisions, ids, clock, new ScoringOptions { EnableShadow = false }, metrics);
        var consumer = new TransactionConsumer(bus, scoring, fs, throwingRepo, deadLetter, ids, clock);

        await bus.WriteAsync(Txn("TX-DL-1"), CancellationToken.None);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var runTask = Task.Run(() => consumer.RunPartitionAsync(0, cts.Token));
        // Give the consumer a moment
        await Task.Delay(300);
        bus.Complete();
        await Task.WhenAny(runTask, Task.Delay(2000));

        Assert.Single(deadLetter.Items);
        Assert.Contains("boom", deadLetter.Items[0].Reason);
    }

    private sealed class ThrowingTransactionRepository : Application.Abstractions.ITransactionRepository
    {
        public Task AddAsync(Transaction transaction, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<Transaction?> GetByRefAsync(string transactionRef, CancellationToken ct = default) => Task.FromResult<Transaction?>(null);
        public Task<Transaction?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Transaction?>(null);
        public Task<IReadOnlyList<Transaction>> ListAsync(int page, int pageSize, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(Array.Empty<Transaction>());
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Transaction>> ListRecentByCustomerAsync(string customerId, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(Array.Empty<Transaction>());
        public Task<IReadOnlyList<Transaction>> ListAllForReplayAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(Array.Empty<Transaction>());
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
