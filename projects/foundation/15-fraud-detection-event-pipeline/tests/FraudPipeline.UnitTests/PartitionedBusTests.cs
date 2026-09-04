using FraudPipeline.Application.Ingestion;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.UnitTests;

public class PartitionedBusTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Transaction Txn(string cardId, int seq)
        => new(
            id: Guid.NewGuid(),
            transactionRef: $"TX-{cardId}-{seq:D5}",
            cardId: cardId,
            customerId: "CUST-" + cardId,
            deviceId: "D1",
            ipAddress: "192.0.2.1",
            merchantId: "M1",
            mcc: "5411",
            amount: Money.Of(100m, "USD"),
            type: TransactionType.CardNotPresent,
            location: GeoLocation.Of(40.71, -74.00, "US"),
            occurredAt: _now.AddSeconds(seq),
            receivedAt: _now.AddSeconds(seq + 1));

    [Fact]
    public async Task PartitionOf_IsDeterministicForSameCard()
    {
        await using var bus = new PartitionedTransactionBus(new IngestionOptions { PartitionCount = 8 });
        var p1 = bus.PartitionOf("CARD1");
        var p2 = bus.PartitionOf("CARD1");
        Assert.Equal(p1, p2);
    }

    [Fact]
    public async Task Enqueue_TwoTxnsSameCard_LandInSamePartition()
    {
        await using var bus = new PartitionedTransactionBus(new IngestionOptions { PartitionCount = 8 });
        var t1 = Txn("CARD1", 1);
        var t2 = Txn("CARD1", 2);
        await bus.WriteAsync(t1, CancellationToken.None);
        await bus.WriteAsync(t2, CancellationToken.None);
        var p = bus.PartitionOf("CARD1");
        Assert.Equal(2, bus.Lag(p));
    }

    [Fact]
    public async Task PerEntityOrdering_UnderParallelIngestion_IsPreserved()
    {
        await using var bus = new PartitionedTransactionBus(new IngestionOptions { PartitionCount = 8 });
        var txns = new List<Transaction>();
        for (int i = 0; i < 100; i++) txns.Add(Txn("CARD1", i));

        // Producers write concurrently
        var tasks = txns.Select(t => bus.WriteAsync(t, CancellationToken.None).AsTask()).ToArray();
        await Task.WhenAll(tasks);

        // Reader on the partition owning CARD1 dequeues; refs must be strictly ascending.
        var p = bus.PartitionOf("CARD1");
        var reader = bus.Reader(p);
        int prevSeq = -1;
        for (int i = 0; i < 100; i++)
        {
            var got = await reader.ReadAsync();
            Assert.Equal("CARD1", got.CardId);
            var seq = int.Parse(got.TransactionRef.Split('-')[^1]);
            Assert.True(seq > prevSeq, $"Expected {seq} > {prevSeq}");
            prevSeq = seq;
        }
    }

    [Fact]
    public async Task TotalLag_AggregatesAcrossPartitions()
    {
        await using var bus = new PartitionedTransactionBus(new IngestionOptions { PartitionCount = 4 });
        for (int i = 0; i < 10; i++) await bus.WriteAsync(Txn("CARD" + (i % 3), i), CancellationToken.None);
        Assert.Equal(10, bus.TotalLag());
    }
}
