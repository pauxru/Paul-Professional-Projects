using Lab.Diagnostics.Measurement;

namespace Lab.UnitTests.Diagnostics;

public sealed class DiagnosticsUtilityTests
{
    [Fact]
    public void LatencyHistogram_SnapshotUsesNearestRankPercentiles()
    {
        var histogram = new LatencyHistogram();
        foreach (var milliseconds in Enumerable.Range(1, 100))
        {
            histogram.Record(TimeSpan.FromMilliseconds(milliseconds));
        }

        var summary = histogram.Snapshot();

        Assert.Equal(100, summary.Count);
        Assert.Equal(50, summary.P50Milliseconds);
        Assert.Equal(95, summary.P95Milliseconds);
        Assert.Equal(99, summary.P99Milliseconds);
    }

    [Fact]
    public void LatencyHistogram_EmptySnapshotIsZeroed()
    {
        var summary = new LatencyHistogram().Snapshot();

        Assert.Equal(0, summary.Count);
        Assert.Equal(0, summary.P95Milliseconds);
    }

    [Fact]
    public void LatencyHistogram_NegativeLatencyIsRejected()
    {
        var histogram = new LatencyHistogram();

        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Record(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void RequestCounter_TracksStartedCompletedAndFailed()
    {
        var counter = new RequestCounter();
        counter.MarkStarted();
        counter.MarkStarted();
        counter.MarkCompleted();
        counter.MarkFailed();

        Assert.Equal(2, counter.Started);
        Assert.Equal(1, counter.Completed);
        Assert.Equal(1, counter.Failed);
    }

    [Fact]
    public void OutboundCallCounter_IsResettable()
    {
        var counter = new OutboundCallCounter();
        counter.Increment();
        counter.Increment();
        counter.Reset();

        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void MeasurementSession_ReportsNonNegativeElapsedTime()
    {
        var session = new MeasurementSession();
        var payload = new byte[1_024];
        payload[0] = 1;
        var result = session.Complete();

        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.True(result.After.AllocatedBytes >= result.Before.AllocatedBytes);
    }

    [Fact]
    public async Task ScenarioReportWriter_WritesMarkdownAndJson()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics-report-writer-test");
        Directory.CreateDirectory(directory);
        try
        {
            var report = new ScenarioReport
            {
                ScenarioId = "INC-999",
                ScenarioName = "Writer test",
                Mode = ScenarioMode.Fixed,
                RequestedOperations = 1,
                StartedAtUtc = DateTimeOffset.UtcNow,
                ElapsedMilliseconds = 1.25,
                Metrics = new Dictionary<string, object?> { ["sample"] = 42 },
                Evidence = ["unit test evidence"]
            };

            var paths = await ScenarioReportWriter.WriteAsync(report, directory, CancellationToken.None);

            Assert.True(File.Exists(paths.JsonPath));
            Assert.True(File.Exists(paths.MarkdownPath));
            Assert.Contains("unit test evidence", await File.ReadAllTextAsync(paths.MarkdownPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ThreadPoolSnapshot_ReportsValidCapacity()
    {
        var snapshot = ThreadPoolSnapshot.Capture();

        Assert.True(snapshot.MaximumWorkerThreads >= snapshot.AvailableWorkerThreads);
        Assert.True(snapshot.BusyWorkerThreads >= 0);
    }
}
