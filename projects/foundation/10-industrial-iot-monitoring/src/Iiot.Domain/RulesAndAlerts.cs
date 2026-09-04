namespace Iiot.Domain;

public sealed record RuleCondition(SensorMetric Metric, RuleComparison Comparison, decimal Threshold);

public sealed record RuleDefinition(
    string RuleId,
    string DeviceId,
    RuleKind Kind,
    SensorMetric Metric,
    RuleComparison Comparison,
    decimal Threshold,
    decimal Hysteresis,
    TimeSpan Dwell,
    TimeSpan Window,
    IReadOnlyList<RuleCondition>? Conditions = null,
    CompositeOperator CompositeOperator = CompositeOperator.And,
    TimeSpan? SuppressionWindow = null,
    bool Enabled = true,
    bool IsMaintenanceSilenced = false);

public sealed record RuleEvaluation(
    bool IsViolation,
    bool ShouldFire,
    bool ShouldResolve,
    string Reason,
    decimal? Statistic = null);

public sealed class Alert
{
    public Alert(string alertId, string ruleId, string deviceId, string reason, DateTimeOffset firedAt)
    {
        AlertId = alertId;
        RuleId = ruleId;
        DeviceId = deviceId;
        Reason = reason;
        FiredAt = firedAt;
        State = AlertState.Firing;
    }

    public string AlertId { get; }
    public string RuleId { get; }
    public string DeviceId { get; }
    public string Reason { get; private set; }
    public DateTimeOffset FiredAt { get; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public AlertState State { get; private set; }

    public void Acknowledge(DateTimeOffset at)
    {
        if (State != AlertState.Firing)
        {
            throw new DomainRuleViolation("Only firing alerts can be acknowledged.");
        }

        State = AlertState.Acknowledged;
        AcknowledgedAt = at;
    }

    public void Resolve(DateTimeOffset at)
    {
        if (State == AlertState.Resolved)
        {
            return;
        }

        State = AlertState.Resolved;
        ResolvedAt = at;
    }

    public void RefreshReason(string reason) => Reason = reason;
}
