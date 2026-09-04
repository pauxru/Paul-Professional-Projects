using Iiot.Domain;

namespace Iiot.Application;

public sealed record EscalationPolicy(TimeSpan EscalateAfter, string Target);

public sealed record AlertEscalation(
    string AlertId,
    string DeviceId,
    string RuleId,
    string Target,
    TimeSpan Age,
    string Reason);

public sealed class AlertEscalationService(IAlertStore store, IClock clock)
{
    public async Task<IReadOnlyList<AlertEscalation>> GetDueAsync(
        EscalationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (policy.EscalateAfter <= TimeSpan.Zero || string.IsNullOrWhiteSpace(policy.Target))
        {
            throw new DomainRuleViolation("Escalation requires a positive duration and target.");
        }

        var active = await store.ListAlertsAsync(true, cancellationToken);
        return active
            .Where(alert => alert.State == AlertState.Firing)
            .Where(alert => clock.UtcNow - alert.FiredAt >= policy.EscalateAfter)
            .Select(alert => new AlertEscalation(
                alert.AlertId,
                alert.DeviceId,
                alert.RuleId,
                policy.Target,
                clock.UtcNow - alert.FiredAt,
                $"Alert has remained firing for {(clock.UtcNow - alert.FiredAt).TotalMinutes:0.0} minutes."))
            .ToArray();
    }
}
