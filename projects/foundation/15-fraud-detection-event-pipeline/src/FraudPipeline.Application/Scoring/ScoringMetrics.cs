using System.Diagnostics.Metrics;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.Scoring;

public sealed class ScoringMetrics
{
    public static readonly string MeterName = "FraudPipeline.Scoring";
    private readonly Meter _meter;
    private readonly Histogram<double> _latency;
    private readonly Counter<long> _decisions;
    private readonly Counter<long> _rulesFired;
    private readonly Counter<long> _budgetExceeded;
    private readonly Counter<long> _shadowDeltas;
    private long _totalDecisions;
    private long _budgetExceededCount;
    private readonly List<double> _latencies = new();
    private readonly object _sampleLock = new();

    public ScoringMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _latency = _meter.CreateHistogram<double>("fraud.scoring.latency_ms", unit: "ms", description: "Scoring latency in milliseconds.");
        _decisions = _meter.CreateCounter<long>("fraud.scoring.decisions", description: "Scoring decisions by band.");
        _rulesFired = _meter.CreateCounter<long>("fraud.scoring.rules_fired", description: "Rules fired count.");
        _budgetExceeded = _meter.CreateCounter<long>("fraud.scoring.budget_exceeded", description: "Times the latency budget was exceeded.");
        _shadowDeltas = _meter.CreateCounter<long>("fraud.scoring.shadow_delta", description: "Difference between live and shadow rulesets by kind.");
    }

    public void RecordDecision(ScoreResult r)
    {
        _latency.Record(r.LatencyMs, new KeyValuePair<string, object?>("decision", r.Decision.ToString()), new KeyValuePair<string, object?>("ruleset", r.RulesetVersion));
        _decisions.Add(1, new KeyValuePair<string, object?>("decision", r.Decision.ToString()));
        _rulesFired.Add(r.RulesFired.Count);
        if (r.BudgetExceeded)
        {
            _budgetExceeded.Add(1);
            Interlocked.Increment(ref _budgetExceededCount);
        }
        Interlocked.Increment(ref _totalDecisions);
        lock (_sampleLock)
        {
            if (_latencies.Count < 100_000) _latencies.Add(r.LatencyMs);
        }
    }

    public void RecordShadow(ScoreResult live, ScoreResult shadow)
    {
        var kind = live.Decision == shadow.Decision ? "match" : "differ";
        _shadowDeltas.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public LatencySummary Summarise()
    {
        double[] arr;
        lock (_sampleLock)
        {
            arr = _latencies.ToArray();
        }
        if (arr.Length == 0) return new LatencySummary(0, 0, 0, 0, 0);
        Array.Sort(arr);
        double At(double p) => arr[(int)Math.Min(arr.Length - 1, Math.Ceiling(arr.Length * p) - 1)];
        return new LatencySummary(arr.Length, At(0.50), At(0.95), At(0.99), _budgetExceededCount);
    }

    public long TotalDecisions => Interlocked.Read(ref _totalDecisions);
    public long BudgetExceededCount => Interlocked.Read(ref _budgetExceededCount);
}

public readonly record struct LatencySummary(int Samples, double P50Ms, double P95Ms, double P99Ms, long BudgetExceededCount);
