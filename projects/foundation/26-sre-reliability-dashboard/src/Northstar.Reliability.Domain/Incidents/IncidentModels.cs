using Northstar.Reliability.Domain.Common;
using Northstar.Reliability.Domain.Slo;
using Northstar.Reliability.Domain.Telemetry;

namespace Northstar.Reliability.Domain.Incidents;

public enum IncidentSeverity
{
    Sev1,
    Sev2,
    Sev3,
    Sev4
}

public enum IncidentStatus
{
    Declared,
    Mitigated,
    Resolved
}

public enum StatusPageState
{
    Investigating,
    Identified,
    Monitoring,
    Resolved
}

public enum IncidentTimelineEventType
{
    Started,
    Detected,
    Declared,
    Acknowledged,
    Mitigation,
    Update,
    Resolution
}

public sealed record IncidentTimelineEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Author,
    IncidentTimelineEventType Type,
    string Message);

public sealed record IncidentBudgetImpact(
    Guid SloId,
    long BadEventsOrMinutes,
    decimal ErrorBudgetConsumed,
    decimal ErrorBudgetImpactPercent);

public sealed record Incident(
    Guid Id,
    string Title,
    IncidentSeverity Severity,
    IReadOnlyList<string> AffectedServices,
    string Commander,
    string CommunicationsLead,
    DateTimeOffset StartedAt,
    IncidentStatus Status,
    StatusPageState StatusPageState,
    IReadOnlyList<Guid> LinkedAlertIds,
    IReadOnlyList<IncidentTimelineEvent> Timeline,
    IReadOnlyList<IncidentBudgetImpact> BudgetImpacts)
{
    public static Incident Declare(
        string title,
        IncidentSeverity severity,
        IEnumerable<string> affectedServices,
        string commander,
        string communicationsLead,
        DateTimeOffset startedAt,
        DateTimeOffset declaredAt,
        string author)
    {
        var services = affectedServices
            .Where(service => !string.IsNullOrWhiteSpace(service))
            .Select(service => service.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(title) || services.Length == 0 ||
            string.IsNullOrWhiteSpace(commander) || string.IsNullOrWhiteSpace(communicationsLead))
        {
            throw new DomainRuleViolationException("An incident requires title, affected services, commander, and communications lead.");
        }

        if (startedAt > declaredAt)
        {
            throw new DomainRuleViolationException("Incident start cannot be after declaration.");
        }

        return new Incident(
            Guid.NewGuid(),
            title.Trim(),
            severity,
            services,
            commander.Trim(),
            communicationsLead.Trim(),
            startedAt,
            IncidentStatus.Declared,
            StatusPageState.Investigating,
            [],
            [
                new IncidentTimelineEvent(Guid.NewGuid(), startedAt, author, IncidentTimelineEventType.Started, "Impact began."),
                new IncidentTimelineEvent(Guid.NewGuid(), declaredAt, author, IncidentTimelineEventType.Declared, "Incident declared.")
            ],
            []);
    }

    public Incident AddTimelineEvent(
        DateTimeOffset occurredAt,
        string author,
        IncidentTimelineEventType type,
        string message)
    {
        if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(message))
        {
            throw new DomainRuleViolationException("Timeline author and message are required.");
        }

        if (occurredAt < StartedAt)
        {
            throw new DomainRuleViolationException("Timeline events cannot precede the incident start.");
        }

        var events = Timeline.Append(new IncidentTimelineEvent(Guid.NewGuid(), occurredAt, author.Trim(), type, message.Trim()))
            .OrderBy(eventItem => eventItem.OccurredAt)
            .ToArray();
        return this with { Timeline = events };
    }

    public Incident Acknowledge(DateTimeOffset at, string author, string message) =>
        AddTimelineEvent(at, author, IncidentTimelineEventType.Acknowledged, message);

    public Incident Mitigate(DateTimeOffset at, string author, string message)
    {
        if (Status != IncidentStatus.Declared)
        {
            throw new DomainRuleViolationException("Only a declared incident can be mitigated.");
        }

        return AddTimelineEvent(at, author, IncidentTimelineEventType.Mitigation, message) with
        {
            Status = IncidentStatus.Mitigated,
            StatusPageState = StatusPageState.Monitoring
        };
    }

    public Incident Resolve(DateTimeOffset at, string author, string message)
    {
        if (Status != IncidentStatus.Mitigated)
        {
            throw new DomainRuleViolationException("An incident must be mitigated before it can be resolved.");
        }

        return AddTimelineEvent(at, author, IncidentTimelineEventType.Resolution, message) with
        {
            Status = IncidentStatus.Resolved,
            StatusPageState = StatusPageState.Resolved
        };
    }

    public Incident LinkAlert(Guid alertId)
    {
        if (alertId == Guid.Empty)
        {
            throw new DomainRuleViolationException("A linked alert identifier is required.");
        }

        return LinkedAlertIds.Contains(alertId)
            ? this
            : this with { LinkedAlertIds = LinkedAlertIds.Append(alertId).ToArray() };
    }

    public Incident AttributeBudgetImpact(IncidentBudgetImpact impact) =>
        this with
        {
            BudgetImpacts = BudgetImpacts
                .Where(existing => existing.SloId != impact.SloId)
                .Append(impact)
                .ToArray()
        };

    public bool IsOpen => Status != IncidentStatus.Resolved;
}

public sealed record IncidentTimingMetrics(
    TimeSpan? MeanTimeToDetect,
    TimeSpan? MeanTimeToAcknowledge,
    TimeSpan? MeanTimeToResolve);

public static class IncidentMetricsCalculator
{
    public static IncidentTimingMetrics Calculate(Incident incident)
    {
        var detected = FirstAt(incident, IncidentTimelineEventType.Detected) ?? FirstAt(incident, IncidentTimelineEventType.Declared);
        var acknowledged = FirstAt(incident, IncidentTimelineEventType.Acknowledged);
        var resolved = FirstAt(incident, IncidentTimelineEventType.Resolution);

        return new IncidentTimingMetrics(
            detected is null ? null : detected.Value - incident.StartedAt,
            detected is null || acknowledged is null ? null : acknowledged.Value - detected.Value,
            detected is null || resolved is null ? null : resolved.Value - detected.Value);
    }

    private static DateTimeOffset? FirstAt(Incident incident, IncidentTimelineEventType type) =>
        incident.Timeline
            .Where(eventItem => eventItem.Type == type)
            .OrderBy(eventItem => eventItem.OccurredAt)
            .Select(eventItem => (DateTimeOffset?)eventItem.OccurredAt)
            .FirstOrDefault();
}

public static class IncidentBudgetAttributor
{
    public static IncidentBudgetImpact Calculate(
        Incident incident,
        SloDefinition slo,
        SliDefinition sli,
        IEnumerable<MetricSample> metrics,
        DateTimeOffset evaluationEnd)
    {
        var end = incident.Timeline
            .Where(eventItem => eventItem.Type == IncidentTimelineEventType.Resolution)
            .OrderByDescending(eventItem => eventItem.OccurredAt)
            .Select(eventItem => (DateTimeOffset?)eventItem.OccurredAt)
            .FirstOrDefault() ?? evaluationEnd;
        var result = SliEvaluator.Evaluate(sli, metrics, incident.StartedAt, end);
        var budget = ErrorBudgetCalculator.Calculate(slo.Target, result);
        var impactPercent = budget.Total == 0m ? 0m : budget.Consumed / budget.Total * 100m;
        return new IncidentBudgetImpact(slo.Id, result.BadEvents, budget.Consumed, impactPercent);
    }
}
