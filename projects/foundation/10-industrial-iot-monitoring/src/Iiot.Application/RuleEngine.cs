using Iiot.Domain;

namespace Iiot.Application;

public sealed class RuleEngine
{
    private readonly Dictionary<string, EvaluationState> _states = new(StringComparer.Ordinal);

    public RuleEvaluation Evaluate(
        RuleDefinition rule,
        TelemetryReading? current,
        IReadOnlyList<TelemetryReading> history,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var key = $"{rule.DeviceId}|{rule.RuleId}";
        if (!_states.TryGetValue(key, out var state))
        {
            state = new EvaluationState();
            _states[key] = state;
        }

        if (!rule.Enabled || rule.IsMaintenanceSilenced)
        {
            var shouldResolve = state.IsFiring;
            state.Reset();
            return new RuleEvaluation(false, false, shouldResolve, rule.IsMaintenanceSilenced ? "Maintenance mode silenced this rule." : "Rule is disabled.");
        }

        var evaluation = EvaluateViolation(rule, current, history, now, state);
        if (evaluation.IsViolation)
        {
            state.ViolationSince ??= now;
            if (!state.IsFiring && now - state.ViolationSince.Value >= rule.Dwell)
            {
                state.IsFiring = true;
                return evaluation with { ShouldFire = true };
            }

            return evaluation;
        }

        var resolve = state.IsFiring;
        state.Reset();
        return evaluation with { ShouldResolve = resolve };
    }

    private static RuleEvaluation EvaluateViolation(
        RuleDefinition rule,
        TelemetryReading? current,
        IReadOnlyList<TelemetryReading> history,
        DateTimeOffset now,
        EvaluationState state)
    {
        return rule.Kind switch
        {
            RuleKind.Threshold => Threshold(rule, current, state),
            RuleKind.RateOfChange => RateOfChange(rule, current, history),
            RuleKind.MissingData => MissingData(rule, history, now),
            RuleKind.Composite => Composite(rule, current),
            _ => throw new ArgumentOutOfRangeException(nameof(rule.Kind))
        };
    }

    private static RuleEvaluation Threshold(RuleDefinition rule, TelemetryReading? current, EvaluationState state)
    {
        if (current is null || current.Quality is QualityFlag.Bad or QualityFlag.Missing)
        {
            return new RuleEvaluation(false, false, false, "No quality telemetry is available for threshold evaluation.");
        }

        var value = current.Values.GetMetric(rule.Metric);
        var violating = CompareWithHysteresis(value, rule.Comparison, rule.Threshold, rule.Hysteresis, state.IsFiring);
        return new RuleEvaluation(
            violating,
            false,
            false,
            $"{rule.Metric}={value:0.###}; threshold={rule.Threshold:0.###}; hysteresis={rule.Hysteresis:0.###}.",
            value);
    }

    private static RuleEvaluation RateOfChange(RuleDefinition rule, TelemetryReading? current, IReadOnlyList<TelemetryReading> history)
    {
        if (current is null)
        {
            return new RuleEvaluation(false, false, false, "No current telemetry is available for rate-of-change evaluation.");
        }

        var window = rule.Window <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : rule.Window;
        var previous = history
            .Where(item => item.DeviceTimestamp < current.DeviceTimestamp && item.DeviceTimestamp >= current.DeviceTimestamp - window)
            .OrderBy(item => item.DeviceTimestamp)
            .FirstOrDefault();
        if (previous is null)
        {
            return new RuleEvaluation(false, false, false, "Insufficient history for rate-of-change evaluation.");
        }

        var elapsedMinutes = Math.Max(0.001, (current.DeviceTimestamp - previous.DeviceTimestamp).TotalMinutes);
        var rate = (current.Values.GetMetric(rule.Metric) - previous.Values.GetMetric(rule.Metric)) / (decimal)elapsedMinutes;
        var violating = Compare(rate, rule.Comparison, rule.Threshold);
        return new RuleEvaluation(violating, false, false, $"{rule.Metric} rate={rate:0.###}/min; threshold={rule.Threshold:0.###}/min.", rate);
    }

    private static RuleEvaluation MissingData(RuleDefinition rule, IReadOnlyList<TelemetryReading> history, DateTimeOffset now)
    {
        var last = history.OrderByDescending(item => item.DeviceTimestamp).FirstOrDefault();
        var timeout = rule.Window <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : rule.Window;
        var age = last is null ? timeout : now - last.DeviceTimestamp;
        var violating = last is null || age >= timeout;
        return new RuleEvaluation(violating, false, false, $"Last heartbeat age={age.TotalSeconds:0}s; allowed={timeout.TotalSeconds:0}s.", (decimal)age.TotalSeconds);
    }

    private static RuleEvaluation Composite(RuleDefinition rule, TelemetryReading? current)
    {
        if (current is null || rule.Conditions is not { Count: > 0 })
        {
            return new RuleEvaluation(false, false, false, "Composite rule needs telemetry and at least one condition.");
        }

        var outcomes = rule.Conditions.Select(condition =>
            Compare(current.Values.GetMetric(condition.Metric), condition.Comparison, condition.Threshold)).ToArray();
        var violating = rule.CompositeOperator == CompositeOperator.And ? outcomes.All(value => value) : outcomes.Any(value => value);
        var expression = string.Join(
            rule.CompositeOperator == CompositeOperator.And ? " AND " : " OR ",
            rule.Conditions.Select(condition => $"{condition.Metric} {condition.Comparison} {condition.Threshold:0.###}"));
        return new RuleEvaluation(violating, false, false, $"Composite ({expression}) evaluated {violating}.", null);
    }

    private static bool CompareWithHysteresis(decimal value, RuleComparison comparison, decimal threshold, decimal hysteresis, bool alreadyFiring)
    {
        return comparison switch
        {
            RuleComparison.GreaterThanOrEqual => value >= (alreadyFiring ? threshold - hysteresis : threshold),
            RuleComparison.LessThanOrEqual => value <= (alreadyFiring ? threshold + hysteresis : threshold),
            _ => false
        };
    }

    private static bool Compare(decimal value, RuleComparison comparison, decimal threshold) =>
        comparison == RuleComparison.GreaterThanOrEqual ? value >= threshold : value <= threshold;

    private sealed class EvaluationState
    {
        public DateTimeOffset? ViolationSince { get; set; }
        public bool IsFiring { get; set; }

        public void Reset()
        {
            ViolationSince = null;
            IsFiring = false;
        }
    }
}
