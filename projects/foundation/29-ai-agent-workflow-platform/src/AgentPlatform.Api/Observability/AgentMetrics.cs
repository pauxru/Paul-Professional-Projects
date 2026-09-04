using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AgentPlatform.Application.Abstractions;

namespace AgentPlatform.Api.Observability;

/// <summary>
/// <see cref="IAgentMetrics"/> implemented over an OpenTelemetry <see cref="Meter"/> and, in
/// parallel, an in-memory snapshot so the console and tests can read current counters without a
/// scraping backend. Thread-safe.
/// </summary>
public sealed class AgentMetrics : IAgentMetrics, IDisposable
{
    public const string MeterName = "AgentPlatform";

    private readonly Meter _meter;
    private readonly Counter<long> _runsStarted;
    private readonly Counter<long> _runsCompleted;
    private readonly Counter<long> _toolCalls;
    private readonly Counter<long> _modelCalls;
    private readonly Counter<long> _budgetHalts;
    private readonly Counter<long> _approvalsRequested;
    private readonly Histogram<double> _stepDuration;
    private readonly Histogram<double> _approvalLatency;

    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);

    public AgentMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _runsStarted = _meter.CreateCounter<long>("agent.runs.started");
        _runsCompleted = _meter.CreateCounter<long>("agent.runs.completed");
        _toolCalls = _meter.CreateCounter<long>("agent.tool.calls");
        _modelCalls = _meter.CreateCounter<long>("agent.model.calls");
        _budgetHalts = _meter.CreateCounter<long>("agent.budget.halts");
        _approvalsRequested = _meter.CreateCounter<long>("agent.approvals.requested");
        _stepDuration = _meter.CreateHistogram<double>("agent.step.duration.ms");
        _approvalLatency = _meter.CreateHistogram<double>("agent.approval.latency.s");
    }

    public void RunStarted(string workflow)
    {
        _runsStarted.Add(1, new KeyValuePair<string, object?>("workflow", workflow));
        Bump($"runs.started:{workflow}");
        Bump("runs.started");
    }

    public void RunCompleted(string workflow, string outcome, double durationSeconds)
    {
        _runsCompleted.Add(1, new KeyValuePair<string, object?>("workflow", workflow), new KeyValuePair<string, object?>("outcome", outcome));
        Bump($"runs.completed:{workflow}:{outcome}");
        Bump($"runs.outcome:{outcome}");
        Bump("runs.completed");
    }

    public void StepExecuted(string workflow, string stepKind, double durationMs)
    {
        _stepDuration.Record(durationMs, new KeyValuePair<string, object?>("workflow", workflow), new KeyValuePair<string, object?>("step", stepKind));
        Bump($"steps:{stepKind}");
    }

    public void ToolInvoked(string tool, bool success)
    {
        _toolCalls.Add(1, new KeyValuePair<string, object?>("tool", tool), new KeyValuePair<string, object?>("success", success));
        Bump($"tool:{tool}:{(success ? "ok" : "error")}");
        if (!success) Bump("tool.errors");
    }

    public void ModelCalled(string model, int tokens)
    {
        _modelCalls.Add(1, new KeyValuePair<string, object?>("model", model));
        Bump("model.calls");
        Add("model.tokens", tokens);
    }

    public void ApprovalRequested(string workflow)
    {
        _approvalsRequested.Add(1, new KeyValuePair<string, object?>("workflow", workflow));
        Bump("approvals.requested");
    }

    public void ApprovalDecided(string workflow, string decision, double latencySeconds)
    {
        _approvalLatency.Record(latencySeconds, new KeyValuePair<string, object?>("workflow", workflow), new KeyValuePair<string, object?>("decision", decision));
        Bump($"approvals.decided:{decision}");
    }

    public void BudgetHalt(string workflow, string reason)
    {
        _budgetHalts.Add(1, new KeyValuePair<string, object?>("workflow", workflow), new KeyValuePair<string, object?>("reason", reason));
        Bump($"budget.halt:{reason}");
        Bump("budget.halts");
    }

    /// <summary>An immutable snapshot of the in-memory counters, for the console and /metrics.</summary>
    public IReadOnlyDictionary<string, long> Snapshot() => new SortedDictionary<string, long>(_counters, StringComparer.Ordinal);

    private void Bump(string key) => _counters.AddOrUpdate(key, 1, (_, v) => v + 1);
    private void Add(string key, long amount) => _counters.AddOrUpdate(key, amount, (_, v) => v + amount);

    public void Dispose() => _meter.Dispose();
}
