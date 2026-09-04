using Northstar.Reliability.Domain.Common;

namespace Northstar.Reliability.Domain.Slo;

public enum AlertSeverity
{
    Ticket,
    Page
}

public enum AlertState
{
    Firing,
    Acknowledged,
    Resolved,
    Suppressed
}

public sealed record BurnWindow(TimeSpan Duration, decimal Threshold);

public sealed record MultiWindowAlertRule(
    string Name,
    AlertSeverity Severity,
    BurnWindow LongWindow,
    BurnWindow ShortWindow,
    decimal IntendedBudgetSpendPercent)
{
    public decimal DerivedBudgetSpendPercent(TimeSpan compliancePeriod) =>
        decimal.Round(
            (decimal)LongWindow.Duration.TotalMinutes / (decimal)compliancePeriod.TotalMinutes *
            LongWindow.Threshold * 100m,
            6);

    public static IReadOnlyList<MultiWindowAlertRule> StandardRules { get; } =
    [
        new("fast-page", AlertSeverity.Page, new BurnWindow(TimeSpan.FromHours(1), 14.4m), new BurnWindow(TimeSpan.FromMinutes(5), 6m), 2m),
        new("slow-page", AlertSeverity.Page, new BurnWindow(TimeSpan.FromHours(6), 6m), new BurnWindow(TimeSpan.FromMinutes(30), 3m), 5m),
        new("sustained-ticket", AlertSeverity.Ticket, new BurnWindow(TimeSpan.FromDays(3), 1m), new BurnWindow(TimeSpan.FromDays(1), 3m), 10m)
    ];
}

public sealed record AlertInstance(
    Guid Id,
    Guid SloId,
    string ServiceSlug,
    string RuleName,
    AlertSeverity Severity,
    AlertState State,
    decimal LongBurnRate,
    decimal ShortBurnRate,
    DateTimeOffset? DetectedAt,
    DateTimeOffset? FiredAt,
    DateTimeOffset? RecoveredAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? SuppressedAt,
    string? SuppressionReason,
    Guid? LinkedIncidentId,
    DateTimeOffset DataObservedAt,
    TimeSpan DetectionLag,
    DateTimeOffset UpdatedAt)
{
    public AlertInstance Acknowledge(DateTimeOffset now) =>
        State == AlertState.Firing
            ? this with { State = AlertState.Acknowledged, AcknowledgedAt = now, UpdatedAt = now }
            : this;

    public AlertInstance LinkIncident(Guid incidentId, DateTimeOffset now) =>
        this with { LinkedIncidentId = incidentId, UpdatedAt = now };
}

public sealed record AlertEvaluationInput(
    Guid SloId,
    string ServiceSlug,
    MultiWindowAlertRule Rule,
    decimal LongBurnRate,
    decimal ShortBurnRate,
    DateTimeOffset Now,
    DateTimeOffset DataObservedAt,
    bool IsMaintenanceActive,
    bool HasOpenIncident);

public static class MultiWindowAlertEvaluator
{
    public static AlertInstance Evaluate(AlertInstance? existing, AlertEvaluationInput input)
    {
        if (input.Rule.LongWindow.Threshold <= 0m || input.Rule.ShortWindow.Threshold <= 0m)
        {
            throw new DomainRuleViolationException("Alert thresholds must be positive.");
        }

        var suppressionReason = input.IsMaintenanceActive
            ? "Declared maintenance window is active."
            : input.HasOpenIncident
                ? "An incident is already open for this service."
                : null;
        var lag = input.Now > input.DataObservedAt ? input.Now - input.DataObservedAt : TimeSpan.Zero;

        if (suppressionReason is not null)
        {
            return NewOrUpdated(
                existing,
                input,
                AlertState.Suppressed,
                existing?.DetectedAt ?? input.Now,
                existing?.FiredAt,
                existing?.RecoveredAt,
                existing?.AcknowledgedAt,
                input.Now,
                suppressionReason,
                lag);
        }

        var breached = input.LongBurnRate >= input.Rule.LongWindow.Threshold &&
                       input.ShortBurnRate >= input.Rule.ShortWindow.Threshold;
        if (breached)
        {
            var state = existing?.State == AlertState.Acknowledged ? AlertState.Acknowledged : AlertState.Firing;
            return NewOrUpdated(
                existing,
                input,
                state,
                existing?.DetectedAt ?? input.Now,
                existing?.FiredAt ?? input.Now,
                null,
                existing?.AcknowledgedAt,
                null,
                null,
                lag);
        }

        if (existing is not null && (existing.State == AlertState.Firing || existing.State == AlertState.Acknowledged))
        {
            return NewOrUpdated(
                existing,
                input,
                AlertState.Resolved,
                existing.DetectedAt,
                existing.FiredAt,
                input.Now,
                existing.AcknowledgedAt,
                null,
                null,
                lag);
        }

        return NewOrUpdated(
            existing,
            input,
            AlertState.Resolved,
            existing?.DetectedAt,
            existing?.FiredAt,
            existing?.RecoveredAt,
            existing?.AcknowledgedAt,
            existing?.SuppressedAt,
            null,
            lag);
    }

    private static AlertInstance NewOrUpdated(
        AlertInstance? existing,
        AlertEvaluationInput input,
        AlertState state,
        DateTimeOffset? detectedAt,
        DateTimeOffset? firedAt,
        DateTimeOffset? recoveredAt,
        DateTimeOffset? acknowledgedAt,
        DateTimeOffset? suppressedAt,
        string? suppressionReason,
        TimeSpan lag) =>
        new(
            existing?.Id ?? Guid.NewGuid(),
            input.SloId,
            input.ServiceSlug,
            input.Rule.Name,
            input.Rule.Severity,
            state,
            input.LongBurnRate,
            input.ShortBurnRate,
            detectedAt,
            firedAt,
            recoveredAt,
            acknowledgedAt,
            suppressedAt,
            suppressionReason,
            existing?.LinkedIncidentId,
            input.DataObservedAt,
            lag,
            input.Now);
}

public sealed record MaintenanceWindow(
    Guid Id,
    string ServiceSlug,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason,
    string DeclaredBy)
{
    public bool IsActiveAt(DateTimeOffset now) => now >= StartsAt && now < EndsAt;

    public static MaintenanceWindow Create(
        string serviceSlug,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string reason,
        string declaredBy)
    {
        if (string.IsNullOrWhiteSpace(serviceSlug) || string.IsNullOrWhiteSpace(reason) ||
            string.IsNullOrWhiteSpace(declaredBy) || endsAt <= startsAt)
        {
            throw new DomainRuleViolationException("Maintenance window requires service, reason, declarer, and a positive duration.");
        }

        return new MaintenanceWindow(Guid.NewGuid(), serviceSlug.Trim().ToLowerInvariant(), startsAt, endsAt, reason.Trim(), declaredBy.Trim());
    }
}

public enum BudgetPolicyAction
{
    Allow,
    Warn,
    FreezeRiskyDeploys,
    FreezeAllChanges
}

public sealed record DeploymentGateDecision(bool Allowed, BudgetPolicyAction Action, string Reason, decimal RemainingBudgetPercent);

public sealed record ErrorBudgetPolicy(decimal WarningRemainingPercent, decimal RiskyDeployFreezeRemainingPercent)
{
    public static ErrorBudgetPolicy Default { get; } = new(50m, 25m);

    public void EnsureValid()
    {
        if (WarningRemainingPercent is <= 0m or > 100m ||
            RiskyDeployFreezeRemainingPercent is <= 0m or >= 100m ||
            RiskyDeployFreezeRemainingPercent >= WarningRemainingPercent)
        {
            throw new DomainRuleViolationException("Policy thresholds must be between zero and 100, with risky-freeze below warning.");
        }
    }
}

public static class ErrorBudgetPolicyEngine
{
    public static DeploymentGateDecision Evaluate(
        string serviceSlug,
        decimal remainingBudgetPercent,
        ErrorBudgetPolicy? policy = null)
    {
        policy ??= ErrorBudgetPolicy.Default;
        policy.EnsureValid();
        if (remainingBudgetPercent <= 0m)
        {
            return new DeploymentGateDecision(false, BudgetPolicyAction.FreezeAllChanges,
                $"{serviceSlug} has exhausted its error budget; all changes are frozen until reliability recovers.", remainingBudgetPercent);
        }

        if (remainingBudgetPercent < policy.RiskyDeployFreezeRemainingPercent)
        {
            return new DeploymentGateDecision(false, BudgetPolicyAction.FreezeRiskyDeploys,
                $"{serviceSlug} has less than {policy.RiskyDeployFreezeRemainingPercent:0.##}% error budget remaining; risky deployments are frozen.", remainingBudgetPercent);
        }

        if (remainingBudgetPercent < policy.WarningRemainingPercent)
        {
            return new DeploymentGateDecision(true, BudgetPolicyAction.Warn,
                $"{serviceSlug} has less than {policy.WarningRemainingPercent:0.##}% error budget remaining; deploy with explicit risk review.", remainingBudgetPercent);
        }

        return new DeploymentGateDecision(true, BudgetPolicyAction.Allow,
            $"{serviceSlug} has healthy error-budget headroom.", remainingBudgetPercent);
    }
}
