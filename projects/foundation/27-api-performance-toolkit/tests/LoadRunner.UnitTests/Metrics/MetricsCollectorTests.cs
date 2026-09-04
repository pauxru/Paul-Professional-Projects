using LoadRunner.Core.Http;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.Metrics;

public class MetricsCollectorTests
{
    [Fact]
    public void ClassifiesErrorsByKind()
    {
        var col = new MetricsCollector();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        col.Record(new RequestSample("A", now, now, now + 10, 10_000_000, 10_000_000, 200, ErrorKind.None, 1, 100));
        col.Record(new RequestSample("A", now, now, now + 10, 10_000_000, 10_000_000, 500, ErrorKind.Http5xx, 1, 100));
        col.Record(new RequestSample("A", now, now, now + 10, 10_000_000, 10_000_000, 429, ErrorKind.Http4xx, 1, 100));
        col.Record(new RequestSample("A", now, now, now + 10, 10_000_000, 10_000_000, 0, ErrorKind.Timeout, 1, 0));
        col.Record(new RequestSample("A", now, now, now + 10, 10_000_000, 10_000_000, 0, ErrorKind.Connection, 1, 0));
        var snap = col.Snapshot(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        Assert.Equal(5, snap.Aggregate.Count);
        Assert.Equal(4, snap.Aggregate.Errors);
        Assert.Equal(1, snap.Aggregate.Http5xx);
        Assert.Equal(1, snap.Aggregate.Http4xx);
        Assert.Equal(1, snap.Aggregate.TimeoutErrors);
        Assert.Equal(1, snap.Aggregate.ConnectionErrors);
    }

    [Fact]
    public void ExcludesWarmupSamples()
    {
        var col = new MetricsCollector();
        var start = DateTimeOffset.UtcNow;
        // Warmup window: first 1000 ms
        var startMs = start.ToUnixTimeMilliseconds();
        // sample in warmup
        col.Record(new RequestSample("A", startMs, startMs, startMs + 500, 500_000_000, 500_000_000, 200, ErrorKind.None, 0, 100));
        // sample after warmup
        col.Record(new RequestSample("A", startMs, startMs, startMs + 1500, 1_000_000, 1_000_000, 200, ErrorKind.None, 0, 100));
        var snap = col.Snapshot(start, start.AddSeconds(2), TimeSpan.FromSeconds(1));
        Assert.Equal(1, snap.Aggregate.Count);
        Assert.Equal(1, snap.OmittedWarmupSamples);
    }

    [Fact]
    public void TracksIntendedLatencyDistinctlyFromServiceLatency()
    {
        var col = new MetricsCollector();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Actual service latency = 20ms, intended = 500ms (400ms delay before we could run it)
        col.Record(new RequestSample("A", now - 480, now - 20, now, 20_000_000, 500_000_000, 200, ErrorKind.None, 0, 100));
        var snap = col.Snapshot(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        Assert.InRange(snap.Aggregate.Service.P95Ms, 15, 25);
        Assert.InRange(snap.Aggregate.Intended.P95Ms, 480, 520);
    }
}
