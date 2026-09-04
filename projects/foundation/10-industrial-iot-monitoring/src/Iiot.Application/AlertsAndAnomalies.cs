using Iiot.Domain;

namespace Iiot.Application;

public sealed class AlertManager
{
    private readonly Dictionary<string, Alert> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new(StringComparer.Ordinal);
    private readonly List<Alert> _history = [];

    public IReadOnlyList<Alert> History => _history;

    public Alert? Apply(
        RuleDefinition rule,
        RuleEvaluation evaluation,
        DateTimeOffset now,
        Func<string> idFactory)
    {
        var key = $"{rule.DeviceId}|{rule.RuleId}";
        if (evaluation.ShouldResolve && _active.Remove(key, out var open))
        {
            open.Resolve(now);
            return open;
        }

        if (!evaluation.ShouldFire)
        {
            return _active.GetValueOrDefault(key);
        }

        if (rule.IsMaintenanceSilenced ||
            (rule.SuppressionWindow is { } suppression &&
             _lastFired.TryGetValue(key, out var last) &&
             now - last < suppression))
        {
            return null;
        }

        if (_active.TryGetValue(key, out var existing))
        {
            existing.RefreshReason(evaluation.Reason);
            return existing;
        }

        var alert = new Alert(idFactory(), rule.RuleId, rule.DeviceId, evaluation.Reason, now);
        _active[key] = alert;
        _history.Add(alert);
        _lastFired[key] = now;
        return alert;
    }

    public Alert Acknowledge(string alertId, DateTimeOffset now)
    {
        var alert = _history.SingleOrDefault(item => item.AlertId == alertId)
            ?? throw new DomainRuleViolation($"Alert '{alertId}' does not exist.");
        alert.Acknowledge(now);
        return alert;
    }
}

public sealed record AnomalyDetection(bool IsAnomaly, string Detector, decimal Statistic, string Reason);

public static class ExplainableAnomalyDetector
{
    public static AnomalyDetection RollingZScore(IReadOnlyList<decimal> baseline, decimal value, decimal threshold = 3m)
    {
        RequireBaseline(baseline, 2);
        var mean = baseline.Average();
        var variance = baseline.Select(item => (item - mean) * (item - mean)).Average();
        var standardDeviation = (decimal)Math.Sqrt((double)variance);
        var score = standardDeviation == 0 ? (value == mean ? 0 : decimal.MaxValue) : (value - mean) / standardDeviation;
        var anomalous = decimal.Abs(score) >= threshold;
        return new AnomalyDetection(anomalous, "rolling-z-score", score, $"value={value:0.###}, mean={mean:0.###}, σ={standardDeviation:0.###}, z={score:0.###}.");
    }

    public static AnomalyDetection MedianAbsoluteDeviation(IReadOnlyList<decimal> baseline, decimal value, decimal threshold = 3.5m)
    {
        RequireBaseline(baseline, 3);
        var median = Median(baseline);
        var deviations = baseline.Select(item => decimal.Abs(item - median)).ToArray();
        var mad = Median(deviations);
        var score = mad == 0 ? (value == median ? 0 : decimal.MaxValue) : 0.6745m * (value - median) / mad;
        var anomalous = decimal.Abs(score) >= threshold;
        return new AnomalyDetection(anomalous, "mad", score, $"value={value:0.###}, median={median:0.###}, MAD={mad:0.###}, modified-z={score:0.###}.");
    }

    public static AnomalyDetection EwmaTrendDeviation(IReadOnlyList<decimal> baseline, decimal value, decimal alpha = 0.2m, decimal tolerance = 3m)
    {
        RequireBaseline(baseline, 2);
        if (alpha is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha));
        }

        var ewma = baseline[0];
        foreach (var sample in baseline.Skip(1))
        {
            ewma = alpha * sample + (1 - alpha) * ewma;
        }

        var residuals = baseline.Select(item => decimal.Abs(item - ewma)).ToArray();
        var scale = residuals.Average();
        var deviation = value - ewma;
        var score = scale == 0 ? (deviation == 0 ? 0 : decimal.MaxValue) : decimal.Abs(deviation) / scale;
        var anomalous = score >= tolerance;
        return new AnomalyDetection(anomalous, "ewma", score, $"value={value:0.###}, EWMA={ewma:0.###}, residual-scale={scale:0.###}, deviation={deviation:0.###}.");
    }

    public static AnomalyDetection SeasonalBaseline(IReadOnlyList<decimal> sameSeasonBaseline, decimal value, decimal tolerance = 3m)
    {
        var z = RollingZScore(sameSeasonBaseline, value, tolerance);
        return z with { Detector = "seasonal-baseline", Reason = $"Same-hour seasonal baseline: {z.Reason}" };
    }

    private static void RequireBaseline(IReadOnlyList<decimal> baseline, int minimum)
    {
        if (baseline.Count < minimum)
        {
            throw new ArgumentException($"At least {minimum} baseline values are required.", nameof(baseline));
        }
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.Order().ToArray();
        var midpoint = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[midpoint - 1] + sorted[midpoint]) / 2 : sorted[midpoint];
    }
}
