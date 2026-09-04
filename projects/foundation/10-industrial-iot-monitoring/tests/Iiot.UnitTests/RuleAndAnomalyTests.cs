using Iiot.Application;
using Iiot.Domain;

namespace Iiot.UnitTests;

public sealed class RuleAndAnomalyTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThresholdRule_OverLimit_FiresImmediatelyWhenNoDwell()
    {
        var engine = new RuleEngine();
        var rule = ThresholdRule();

        var result = engine.Evaluate(rule, Reading(80m), [], Start);

        Assert.True(result.IsViolation);
        Assert.True(result.ShouldFire);
        Assert.Contains("TemperatureC", result.Reason);
    }

    [Fact]
    public void ThresholdRule_Hysteresis_PreventsFlappingUntilRecoveryBand()
    {
        var engine = new RuleEngine();
        var rule = ThresholdRule(hysteresis: 2m);
        var fired = engine.Evaluate(rule, Reading(80m), [], Start);
        var withinBand = engine.Evaluate(rule, Reading(79m), [], Start.AddSeconds(1));
        var recovered = engine.Evaluate(rule, Reading(77m), [], Start.AddSeconds(2));

        Assert.True(fired.ShouldFire);
        Assert.True(withinBand.IsViolation);
        Assert.False(withinBand.ShouldResolve);
        Assert.True(recovered.ShouldResolve);
    }

    [Fact]
    public void ThresholdRule_DwellWaitsForContinuousViolation()
    {
        var engine = new RuleEngine();
        var rule = ThresholdRule(dwell: TimeSpan.FromMinutes(1));

        var initial = engine.Evaluate(rule, Reading(81m), [], Start);
        var early = engine.Evaluate(rule, Reading(81m), [], Start.AddSeconds(59));
        var elapsed = engine.Evaluate(rule, Reading(81m), [], Start.AddMinutes(1));

        Assert.False(initial.ShouldFire);
        Assert.False(early.ShouldFire);
        Assert.True(elapsed.ShouldFire);
    }

    [Fact]
    public void ThresholdRule_NormalValue_ResolvesFiringAlert()
    {
        var engine = new RuleEngine();
        var rule = ThresholdRule();
        engine.Evaluate(rule, Reading(81m), [], Start);

        var result = engine.Evaluate(rule, Reading(70m), [], Start.AddMinutes(1));

        Assert.False(result.IsViolation);
        Assert.True(result.ShouldResolve);
    }

    [Fact]
    public void RateOfChangeRule_RapidTemperatureRise_Fires()
    {
        var engine = new RuleEngine();
        var rule = new RuleDefinition(
            "rate",
            "cmp-01",
            RuleKind.RateOfChange,
            SensorMetric.TemperatureC,
            RuleComparison.GreaterThanOrEqual,
            10m,
            0m,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(5));
        var old = Reading(50m, Start);
        var current = Reading(75m, Start.AddMinutes(1));

        var result = engine.Evaluate(rule, current, [old], Start.AddMinutes(1));

        Assert.True(result.ShouldFire);
        Assert.InRange(result.Statistic!.Value, 24.99m, 25.01m);
    }

    [Fact]
    public void MissingDataRule_HeartbeatOlderThanWindow_Fires()
    {
        var engine = new RuleEngine();
        var rule = new RuleDefinition(
            "missing",
            "cmp-01",
            RuleKind.MissingData,
            SensorMetric.TemperatureC,
            RuleComparison.GreaterThanOrEqual,
            0m,
            0m,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(5));

        var result = engine.Evaluate(rule, null, [Reading(58m, Start)], Start.AddMinutes(6));

        Assert.True(result.ShouldFire);
        Assert.True(result.IsViolation);
    }

    [Fact]
    public void CompositeAndRule_RequiresAllSignals()
    {
        var engine = new RuleEngine();
        var rule = CompositeRule(CompositeOperator.And);

        var notEnough = engine.Evaluate(rule, Reading(80m, Start, vibration: 2m), [], Start);
        var both = engine.Evaluate(rule, Reading(80m, Start, vibration: 6m), [], Start.AddSeconds(1));

        Assert.False(notEnough.IsViolation);
        Assert.True(both.ShouldFire);
    }

    [Fact]
    public void CompositeOrRule_FiresWhenOneSignalMatches()
    {
        var engine = new RuleEngine();
        var rule = CompositeRule(CompositeOperator.Or);

        var result = engine.Evaluate(rule, Reading(70m, Start, vibration: 6m), [], Start);

        Assert.True(result.ShouldFire);
    }

    [Fact]
    public void AlertManager_RepeatedFiringEvaluation_DeduplicatesOpenAlert()
    {
        var manager = new AlertManager();
        var rule = ThresholdRule();
        var evaluation = new RuleEvaluation(true, true, false, "hot");

        var first = manager.Apply(rule, evaluation, Start, () => "a-1");
        var second = manager.Apply(rule, evaluation, Start.AddSeconds(1), () => "a-2");

        Assert.Same(first, second);
        Assert.Single(manager.History);
    }

    [Fact]
    public void AlertManager_SuppressionWindow_SuppressesRefireAfterResolution()
    {
        var manager = new AlertManager();
        var rule = ThresholdRule() with { SuppressionWindow = TimeSpan.FromMinutes(10) };
        var fired = manager.Apply(rule, new RuleEvaluation(true, true, false, "hot"), Start, () => "a-1");
        manager.Apply(rule, new RuleEvaluation(false, false, true, "normal"), Start.AddSeconds(1), () => "unused");

        var suppressed = manager.Apply(rule, new RuleEvaluation(true, true, false, "hot again"), Start.AddMinutes(2), () => "a-2");

        Assert.NotNull(fired);
        Assert.Equal(AlertState.Resolved, fired!.State);
        Assert.Null(suppressed);
        Assert.Single(manager.History);
    }

    [Fact]
    public void AlertManager_MaintenanceMode_DoesNotOpenAlert()
    {
        var manager = new AlertManager();
        var rule = ThresholdRule() with { IsMaintenanceSilenced = true };

        var result = manager.Apply(rule, new RuleEvaluation(true, true, false, "hot"), Start, () => "a-1");

        Assert.Null(result);
        Assert.Empty(manager.History);
    }

    [Fact]
    public void Alert_AcknowledgeThenResolve_FollowsLifecycle()
    {
        var alert = new Alert("a", "r", "cmp-01", "hot", Start);
        alert.Acknowledge(Start.AddMinutes(1));
        alert.Resolve(Start.AddMinutes(2));

        Assert.Equal(AlertState.Resolved, alert.State);
        Assert.NotNull(alert.AcknowledgedAt);
        Assert.NotNull(alert.ResolvedAt);
    }

    [Fact]
    public async Task AlertEscalation_FiringAlertBeyondPolicy_IsReturnedForOnCall()
    {
        var store = new MemoryAlertStore();
        await store.SaveAlertAsync(new AlertSnapshot(
            "a-1",
            "temperature",
            "cmp-01",
            "hot",
            AlertState.Firing,
            Start,
            null,
            null));
        var service = new AlertEscalationService(store, new FakeClock(Start.AddMinutes(16)));

        var due = await service.GetDueAsync(new EscalationPolicy(TimeSpan.FromMinutes(15), "maintenance-on-call"));

        var escalation = Assert.Single(due);
        Assert.Equal("maintenance-on-call", escalation.Target);
        Assert.InRange(escalation.Age.TotalMinutes, 15.99, 16.01);
    }

    [Fact]
    public void RollingZScore_Spike_IsExplainablyDetected()
    {
        var result = ExplainableAnomalyDetector.RollingZScore([10m, 10.1m, 9.9m, 10.05m, 9.95m], 16m);

        Assert.True(result.IsAnomaly);
        Assert.True(decimal.Abs(result.Statistic) > 3m);
        Assert.Contains("mean", result.Reason);
    }

    [Fact]
    public void Mad_Spike_IsExplainablyDetected()
    {
        var result = ExplainableAnomalyDetector.MedianAbsoluteDeviation([2m, 2.1m, 1.9m, 2.05m, 1.95m], 8m);

        Assert.True(result.IsAnomaly);
        Assert.Equal("mad", result.Detector);
    }

    [Fact]
    public void Ewma_TrendDeviation_IsExplainablyDetected()
    {
        var result = ExplainableAnomalyDetector.EwmaTrendDeviation([50m, 50.2m, 49.9m, 50.1m, 50m], 60m);

        Assert.True(result.IsAnomaly);
        Assert.Equal("ewma", result.Detector);
    }

    [Fact]
    public void SeasonalBaseline_OutOfSeasonValue_IsDetected()
    {
        var result = ExplainableAnomalyDetector.SeasonalBaseline([58m, 59m, 57m, 58.5m, 57.5m], 70m);

        Assert.True(result.IsAnomaly);
        Assert.Equal("seasonal-baseline", result.Detector);
        Assert.Contains("Same-hour", result.Reason);
    }

    [Fact]
    public void AnomalyDetector_NormalValue_IsNotFlagged()
    {
        var result = ExplainableAnomalyDetector.RollingZScore([10m, 10.2m, 9.8m, 10.1m, 9.9m], 10.05m);

        Assert.False(result.IsAnomaly);
    }

    private static RuleDefinition ThresholdRule(decimal hysteresis = 0m, TimeSpan? dwell = null) =>
        new(
            "temperature",
            "cmp-01",
            RuleKind.Threshold,
            SensorMetric.TemperatureC,
            RuleComparison.GreaterThanOrEqual,
            80m,
            hysteresis,
            dwell ?? TimeSpan.Zero,
            TimeSpan.Zero);

    private static RuleDefinition CompositeRule(CompositeOperator @operator) =>
        new(
            "composite",
            "cmp-01",
            RuleKind.Composite,
            SensorMetric.TemperatureC,
            RuleComparison.GreaterThanOrEqual,
            0m,
            0m,
            TimeSpan.Zero,
            TimeSpan.Zero,
            [
                new RuleCondition(SensorMetric.TemperatureC, RuleComparison.GreaterThanOrEqual, 75m),
                new RuleCondition(SensorMetric.VibrationMmPerSecondRms, RuleComparison.GreaterThanOrEqual, 5m)
            ],
            @operator);

    private static TelemetryReading Reading(decimal temperature, DateTimeOffset? timestamp = null, decimal vibration = 2m) =>
        new(
            "cmp-01",
            1,
            timestamp ?? Start,
            new TelemetryValues(temperature, vibration, 7.4m, 26m, 185m, 64m, MachineState.Running));
}
