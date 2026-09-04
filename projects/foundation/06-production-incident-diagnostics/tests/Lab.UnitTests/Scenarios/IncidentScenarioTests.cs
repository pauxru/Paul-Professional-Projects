using Lab.Diagnostics.Measurement;
using Lab.Scenarios;

namespace Lab.UnitTests.Scenarios;

[Trait("Category", "Incident")]
public sealed class IncidentScenarioTests
{
    [Fact]
    public async Task NPlusOne_BrokenIssuesAtLeastTenTimesTheFixedSqlCommands()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-001", requests: 10);

        Assert.True(MetricLong(broken, "sqlRoundTrips") >= MetricLong(fixedReport, "sqlRoundTrips") * 10);
        Assert.Equal(10, MetricLong(broken, "shipmentsRead"));
        Assert.Equal(10, MetricLong(fixedReport, "shipmentsRead"));
    }

    [Fact]
    public async Task MissingIndex_BrokenPlanScansAndFixedPlanUsesIndex()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-002", requests: 2);

        Assert.Contains("SCAN", MetricString(broken, "sqliteQueryPlan"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INDEX", MetricString(fixedReport, "sqliteQueryPlan"), StringComparison.OrdinalIgnoreCase);
        Assert.True(MetricDouble(broken, "queryP95Milliseconds") > MetricDouble(fixedReport, "queryP95Milliseconds"));
    }

    [Fact]
    public async Task ConnectionPoolExhaustion_BrokenTimesOutAndFixedCompletes()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-003", requests: 8);

        Assert.True(MetricLong(broken, "acquireTimeouts") > 0);
        Assert.Equal(0, MetricLong(fixedReport, "acquireTimeouts"));
        Assert.Equal(8, MetricLong(fixedReport, "completedOperations"));
        Assert.Equal(0, MetricLong(broken, "leasedAfterCleanup"));
    }

    [Fact]
    public async Task MemoryLeak_BrokenRetainsPayloadsAndFixedReleasesThem()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-004", requests: 8);

        Assert.Equal(8, MetricLong(broken, "retainedPayloads"));
        Assert.Equal(0, MetricLong(fixedReport, "retainedPayloads"));
        Assert.True(MetricLong(broken, "staticHandlerSubscriptions") > 0);
    }

    [Fact]
    public async Task BlockingAsync_BrokenOccupiesMoreThreadPoolWorkers()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-005", requests: 20);

        Assert.True(MetricBool(broken, "syncWaitUsed"));
        Assert.False(MetricBool(fixedReport, "syncWaitUsed"));
        Assert.True(MetricLong(broken, "blockedWorkerMilliseconds") > 0);
        Assert.Equal(0, MetricLong(fixedReport, "blockedWorkerMilliseconds"));
    }

    [Fact]
    public async Task ThreadPoolStarvation_BrokenHasGreaterQueueDelay()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-006", requests: 16);

        Assert.True(MetricDouble(broken, "queueDelayP95Milliseconds") > MetricDouble(fixedReport, "queueDelayP95Milliseconds"));
        Assert.Equal(2, MetricLong(broken, "boundedConcurrency"));
        Assert.Equal(4, MetricLong(fixedReport, "boundedConcurrency"));
    }

    [Fact]
    public async Task DownstreamTimeout_BrokenAccumulatesMoreLatencyAndPileUp()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-007", requests: 16);

        Assert.True(MetricDouble(broken, "p95LatencyMilliseconds") > MetricDouble(fixedReport, "p95LatencyMilliseconds"));
        Assert.True(MetricLong(broken, "peakDependencyPileUp") > MetricLong(fixedReport, "peakDependencyPileUp"));
        Assert.True(MetricLong(fixedReport, "timeoutResponses") > 0);
    }

    [Fact]
    public async Task RetryStorm_BrokenAmplifiesDownstreamCallsAtLeastEightFold()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-008", requests: 3);

        Assert.True(MetricLong(broken, "actualDownstreamCalls") >= MetricLong(fixedReport, "actualDownstreamCalls") * 8);
        Assert.Equal(81, MetricLong(broken, "actualDownstreamCalls"));
        Assert.True(MetricLong(fixedReport, "circuitRejectedRequests") > 0);
    }

    [Fact]
    public async Task PoisonQueue_BrokenBlocksPartitionAndFixedDeadLettersPoison()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-009", requests: 5);

        Assert.Equal(0, MetricLong(broken, "healthyMessagesProcessed"));
        Assert.Equal(5, MetricLong(fixedReport, "healthyMessagesProcessed"));
        Assert.Equal(1, MetricLong(fixedReport, "deadLettered"));
    }

    [Fact]
    public async Task CacheStampede_BrokenCallsOriginAtLeastFiveTimesMore()
    {
        var (broken, fixedReport) = await RunPairAsync("INC-010", requests: 10);

        Assert.True(MetricLong(broken, "originLoads") >= MetricLong(fixedReport, "originLoads") * 5);
        Assert.Equal(1, MetricLong(fixedReport, "originLoads"));
    }

    private static async Task<(ScenarioReport Broken, ScenarioReport Fixed)> RunPairAsync(string scenarioId, int requests)
    {
        var catalog = new ScenarioCatalog();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var broken = await catalog.RunAsync(
            new ScenarioRunOptions(scenarioId, ScenarioMode.Broken, requests, TimeSpan.FromSeconds(4)),
            timeout.Token);
        var fixedReport = await catalog.RunAsync(
            new ScenarioRunOptions(scenarioId, ScenarioMode.Fixed, requests, TimeSpan.FromSeconds(4)),
            timeout.Token);
        return (broken, fixedReport);
    }

    private static long MetricLong(ScenarioReport report, string name) => Convert.ToInt64(report.Metrics[name]);

    private static double MetricDouble(ScenarioReport report, string name) => Convert.ToDouble(report.Metrics[name]);

    private static string MetricString(ScenarioReport report, string name) => Convert.ToString(report.Metrics[name]) ?? string.Empty;

    private static bool MetricBool(ScenarioReport report, string name) => Convert.ToBoolean(report.Metrics[name]);
}
