using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Synthetic;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.ValueObjects;
using FraudPipeline.Infrastructure.Time;

namespace FraudPipeline.UnitTests;

public class FeatureStoreReplayTests
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Transaction Txn(string card, DateTimeOffset at, decimal amount, string merchant, string device, string ip)
        => new(
            id: Guid.NewGuid(),
            transactionRef: $"TX{Guid.NewGuid().ToString("N")[..12]}",
            cardId: card,
            customerId: "CUST" + card[^3..],
            deviceId: device,
            ipAddress: ip,
            merchantId: merchant,
            mcc: "5411",
            amount: Money.Of(amount, "USD"),
            type: TransactionType.CardNotPresent,
            location: GeoLocation.Of(40.7128, -74.0060, "US"),
            occurredAt: at,
            receivedAt: at.AddSeconds(1));

    [Fact]
    public void Rebuild_FromReplay_ReproducesLiveAggregates()
    {
        var clock = new FakeClock(_start);
        var runtime1 = new FeatureStoreRuntime(clock);
        var svc1 = new FeatureStoreService(runtime1);

        var txns = new List<Transaction>();
        for (int i = 0; i < 30; i++)
        {
            var t = Txn("CARD001", _start.AddSeconds(i * 30), 100 + i, $"M{i % 3}", "D1", "10.0.0.1");
            txns.Add(t);
            svc1.Observe(t);
        }

        var live = svc1.Snapshot(txns[^1], _start.AddMinutes(20));

        // Rebuild in a fresh runtime by replaying the same events.
        var runtime2 = new FeatureStoreRuntime(clock);
        var svc2 = new FeatureStoreService(runtime2);
        svc2.Rebuild(txns);
        var replayed = svc2.Snapshot(txns[^1], _start.AddMinutes(20));

        Assert.Equal(live.TxnCount1h, replayed.TxnCount1h);
        Assert.Equal(live.AmountSum1h, replayed.AmountSum1h);
        Assert.Equal(live.DistinctMerchants1h, replayed.DistinctMerchants1h);
        Assert.Equal(live.AmountAvg7d, replayed.AmountAvg7d);
    }
}
