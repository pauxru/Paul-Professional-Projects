using System.Diagnostics.Metrics;
using JobScheduler.Application.Abstractions;

namespace JobScheduler.Infrastructure.Metrics;

/// <summary>
/// OpenTelemetry <see cref="Meter"/>-backed metrics for the scheduler. Exposes claim latency,
/// queue/DLQ depth gauges, run-duration histogram per definition, and counters for claims,
/// lease expiries and leadership changes.
/// </summary>
public sealed class SchedulerMetrics : ISchedulerMetrics, IDisposable
{
    public const string MeterName = "JobScheduler";

    private readonly Meter _meter;
    private readonly Histogram<double> _claimLatency;
    private readonly Histogram<double> _runDuration;
    private readonly Counter<long> _runsClaimed;
    private readonly Counter<long> _leaseExpiries;
    private readonly Counter<long> _leadershipChanges;

    private int _queueDepth;
    private int _dlqDepth;

    public SchedulerMetrics()
    {
        _meter = new Meter(MeterName);
        _claimLatency = _meter.CreateHistogram<double>("scheduler.claim.latency", "ms", "Latency to claim a due run");
        _runDuration = _meter.CreateHistogram<double>("scheduler.run.duration", "s", "Run execution duration");
        _runsClaimed = _meter.CreateCounter<long>("scheduler.runs.claimed", "runs", "Runs claimed by workers");
        _leaseExpiries = _meter.CreateCounter<long>("scheduler.lease.expiries", "leases", "Leases that expired and were reclaimed");
        _leadershipChanges = _meter.CreateCounter<long>("scheduler.leadership.changes", "events", "Leadership acquisitions");
        _meter.CreateObservableGauge("scheduler.queue.depth", () => _queueDepth, "runs", "Pending runs awaiting a worker");
        _meter.CreateObservableGauge("scheduler.dlq.depth", () => _dlqDepth, "runs", "Unreplayed dead-letter entries");
    }

    public void RecordClaimLatency(double milliseconds) => _claimLatency.Record(milliseconds);

    public void RecordRunDuration(string jobName, double seconds, bool success) =>
        _runDuration.Record(seconds, new KeyValuePair<string, object?>("job", jobName), new KeyValuePair<string, object?>("success", success));

    public void SetQueueDepth(int depth) => _queueDepth = depth;
    public void SetDlqDepth(int depth) => _dlqDepth = depth;
    public void RunClaimed() => _runsClaimed.Add(1);
    public void LeaseExpired(int count = 1) => _leaseExpiries.Add(count);
    public void LeadershipChanged(string? newOwner) => _leadershipChanges.Add(1, new KeyValuePair<string, object?>("owner", newOwner ?? "none"));

    public void Dispose() => _meter.Dispose();
}
