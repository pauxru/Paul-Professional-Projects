using Iiot.Domain;

namespace Iiot.Application;

public sealed class AlertRuleOrchestrator(
    IRuleStore ruleStore,
    IAlertStore alertStore,
    ITelemetryStore telemetryStore,
    RuleEngine ruleEngine,
    IClock clock)
{
    public async Task<IReadOnlyList<AlertSnapshot>> EvaluateAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        var rules = await ruleStore.ListRulesAsync(reading.DeviceId, cancellationToken);
        var history = await telemetryStore.QueryTelemetryAsync(
            reading.DeviceId,
            reading.DeviceTimestamp - TimeSpan.FromHours(24),
            reading.DeviceTimestamp,
            cancellationToken);
        var changed = new List<AlertSnapshot>();

        foreach (var rule in rules)
        {
            var evaluation = ruleEngine.Evaluate(rule, reading, history, reading.DeviceTimestamp);
            var current = await alertStore.FindActiveAlertAsync(rule.RuleId, rule.DeviceId, cancellationToken);
            if (evaluation.ShouldResolve && current is not null)
            {
                var resolved = current with { State = AlertState.Resolved, ResolvedAt = reading.DeviceTimestamp };
                await alertStore.SaveAlertAsync(resolved, cancellationToken);
                changed.Add(resolved);
                continue;
            }

            if (!evaluation.ShouldFire || current is not null || rule.IsMaintenanceSilenced)
            {
                continue;
            }

            if (rule.SuppressionWindow is { } suppression)
            {
                var latest = (await alertStore.ListAlertsAsync(false, cancellationToken))
                    .Where(item => item.RuleId == rule.RuleId && item.DeviceId == rule.DeviceId)
                    .OrderByDescending(item => item.FiredAt)
                    .FirstOrDefault();
                if (latest is not null && reading.DeviceTimestamp - latest.FiredAt < suppression)
                {
                    continue;
                }
            }

            var alert = new AlertSnapshot(
                Guid.NewGuid().ToString("N"),
                rule.RuleId,
                rule.DeviceId,
                evaluation.Reason,
                AlertState.Firing,
                reading.DeviceTimestamp,
                null,
                null);
            await alertStore.SaveAlertAsync(alert, cancellationToken);
            changed.Add(alert);
        }

        return changed;
    }

    public async Task<AlertSnapshot> AcknowledgeAsync(string alertId, CancellationToken cancellationToken = default)
    {
        var alert = await alertStore.FindAlertAsync(alertId, cancellationToken)
            ?? throw new DomainRuleViolation($"Alert '{alertId}' does not exist.");
        if (alert.State != AlertState.Firing)
        {
            throw new DomainRuleViolation("Only firing alerts can be acknowledged.");
        }

        var acknowledged = alert with { State = AlertState.Acknowledged, AcknowledgedAt = clock.UtcNow };
        await alertStore.SaveAlertAsync(acknowledged, cancellationToken);
        return acknowledged;
    }
}
