using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SavannaLogistics.Application;
using SavannaLogistics.Infrastructure;

namespace SavannaLogistics.UnitTests;

public sealed class EventOrderingAndDedupTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Watermark_OutOfOrderWithinWindow_EmitsBySequence()
    {
        var vehicle = Guid.NewGuid();
        var buffer = new WatermarkReorderBuffer(TimeSpan.FromSeconds(5));
        buffer.Add(TestData.Ping(vehicle, 1, Start));
        buffer.Add(TestData.Ping(vehicle, 3, Start.AddSeconds(4)));
        buffer.Add(TestData.Ping(vehicle, 2, Start.AddSeconds(2)));

        var result = buffer.Add(TestData.Ping(vehicle, 4, Start.AddSeconds(10)));

        Assert.Equal(new long[] { 1, 2, 3 }, result.Ready.Select(ping => ping.SequenceNumber));
        Assert.Empty(result.Late);
    }

    [Fact]
    public void Watermark_HopelesslyLateEvent_GoesToLateOutput()
    {
        var vehicle = Guid.NewGuid();
        var buffer = new WatermarkReorderBuffer(TimeSpan.FromSeconds(5));
        buffer.Add(TestData.Ping(vehicle, 1, Start));
        buffer.Add(TestData.Ping(vehicle, 3, Start.AddSeconds(10)));

        var result = buffer.Add(TestData.Ping(vehicle, 2, Start.AddSeconds(2)));

        Assert.Single(result.Late);
        Assert.Equal(2, result.Late[0].SequenceNumber);
    }

    [Fact]
    public void Watermark_FlushExpired_ReleasesQuietVehicle()
    {
        var vehicle = Guid.NewGuid();
        var buffer = new WatermarkReorderBuffer(TimeSpan.FromSeconds(5));
        buffer.Add(TestData.Ping(vehicle, 4, Start));
        var result = buffer.FlushExpired(Start.AddSeconds(6));
        Assert.Equal(4, Assert.Single(result.Ready).SequenceNumber);
    }

    [Fact]
    public void Watermark_DifferentVehicles_HaveIndependentOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var buffer = new WatermarkReorderBuffer(TimeSpan.Zero);
        Assert.Equal(1, Assert.Single(buffer.Add(TestData.Ping(first, 1, Start)).Ready).SequenceNumber);
        Assert.Equal(9, Assert.Single(buffer.Add(TestData.Ping(second, 9, Start)).Ready).SequenceNumber);
    }

    [Fact]
    public void DedupCache_SameKeyAndContent_IsDuplicate()
    {
        var clock = new FakeClock(Start);
        var cache = new ExpiringLruDeduplicator(10, TimeSpan.FromMinutes(1), clock);
        var vehicle = Guid.NewGuid();
        Assert.Equal(DeduplicationDecision.New, cache.CheckAndRemember(vehicle, 1, "A"));
        Assert.Equal(DeduplicationDecision.Duplicate, cache.CheckAndRemember(vehicle, 1, "A"));
    }

    [Fact]
    public void DedupCache_SameKeyDifferentContent_IsConflict()
    {
        var cache = new ExpiringLruDeduplicator(10, TimeSpan.FromMinutes(1), new FakeClock(Start));
        var vehicle = Guid.NewGuid();
        cache.CheckAndRemember(vehicle, 1, "A");
        Assert.Equal(DeduplicationDecision.Conflict, cache.CheckAndRemember(vehicle, 1, "B"));
    }

    [Fact]
    public void DedupCache_ExpiresAndEvictsLeastRecentlyUsed()
    {
        var clock = new FakeClock(Start);
        var cache = new ExpiringLruDeduplicator(2, TimeSpan.FromSeconds(5), clock);
        var vehicle = Guid.NewGuid();
        cache.CheckAndRemember(vehicle, 1, "A");
        cache.CheckAndRemember(vehicle, 2, "B");
        cache.CheckAndRemember(vehicle, 1, "A");
        cache.CheckAndRemember(vehicle, 3, "C");
        Assert.Equal(DeduplicationDecision.New, cache.CheckAndRemember(vehicle, 2, "B"));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(DeduplicationDecision.New, cache.CheckAndRemember(vehicle, 1, "A"));
    }

    [Fact]
    public void ContentHash_ChangesWhenTelemetryContentChanges()
    {
        var vehicle = Guid.NewGuid();
        var baseline = new VehiclePingInput(vehicle, -1.2, 36.8, 40, 90, 10, 50, true, Start, 1);
        Assert.NotEqual(
            PingContentHasher.Hash(baseline),
            PingContentHasher.Hash(baseline with { SpeedKph = 41 }));
    }

    [Fact]
    public async Task DatabaseUniqueIndex_SuppressesDuplicateAndDetectsConflict()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LogisticsDbContext>().UseSqlite(connection).Options;
        await using var db = new LogisticsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var repository = new EfLogisticsRepository(db, new DatabaseWriteGate());
        var vehicle = Guid.NewGuid();
        await repository.AddVehicleAsync(
            new SavannaLogistics.Domain.Vehicle(vehicle, "KDB-001", "Isuzu", "NPR", 4_500, 24),
            default);
        var first = TestData.Ping(vehicle, 11, Start, hash: "A");
        var duplicate = TestData.Ping(vehicle, 11, Start, hash: "A");
        var conflict = TestData.Ping(vehicle, 11, Start, speed: 50, hash: "B");

        Assert.Equal(PingPersistenceResult.Inserted, await repository.TryAddPingAsync(first, default));
        Assert.Equal(PingPersistenceResult.Duplicate, await repository.TryAddPingAsync(duplicate, default));
        Assert.Equal(PingPersistenceResult.Conflict, await repository.TryAddPingAsync(conflict, default));
        Assert.Equal(1, await repository.CountPingsAsync(default));
    }
}
