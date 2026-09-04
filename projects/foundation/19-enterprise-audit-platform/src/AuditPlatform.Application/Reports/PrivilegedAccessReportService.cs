using AuditPlatform.Application.Abstractions;
using AuditPlatform.Domain.Events;

namespace AuditPlatform.Application.Reports;

public sealed record PrivilegedAccessAnomaly(
    string Type,
    Guid EventId,
    string ActorId,
    string ActorDisplayName,
    DateTimeOffset EventTime,
    string Description);

public sealed record PrivilegedAccessReport(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Events,
    IReadOnlyList<PrivilegedAccessAnomaly> Anomalies);

/// <summary>
/// Privileged-access anomaly detector. Deliberately explicit — the rules describe classes of
/// event patterns operators actually watch for. Extensions welcome; the rules are pluggable in
/// principle but this project keeps them simple and testable.
/// </summary>
public sealed class PrivilegedAccessReportService
{
    private readonly IAuditEventStore _events;

    public PrivilegedAccessReportService(IAuditEventStore events) => _events = events;

    public PrivilegedAccessReport Build(string tenantId, DateTimeOffset from, DateTimeOffset to)
    {
        var events = _events.Query(tenantId)
            .Where(e => e.EventTime >= from && e.EventTime <= to)
            .Where(e => e.Category == EventCategory.PrivilegedAccess || e.Severity == EventSeverity.Critical)
            .OrderBy(e => e.EventTime)
            .ToList();

        var anomalies = new List<PrivilegedAccessAnomaly>();

        // Rule 1 — out-of-hours (before 06:00 or after 21:00 UTC).
        foreach (var e in events)
        {
            var hour = e.EventTime.UtcDateTime.Hour;
            if (hour < 6 || hour >= 21)
                anomalies.Add(new PrivilegedAccessAnomaly("out-of-hours", e.Id, e.ActorId, e.ActorDisplayName, e.EventTime,
                    $"Privileged action at {hour:00}:{e.EventTime.UtcDateTime.Minute:00} UTC"));
        }

        // Rule 2 — first-time actor on resource: within this window, an actor touches a resource
        // they have never touched before *inside the window*.
        var seenPairs = new HashSet<(string, string, string)>();
        foreach (var e in events)
        {
            var key = (e.ActorId, e.ResourceType, e.ResourceId);
            if (seenPairs.Add(key) && seenPairs.Count > 1)
            {
                anomalies.Add(new PrivilegedAccessAnomaly("first-time-access", e.Id, e.ActorId, e.ActorDisplayName, e.EventTime,
                    $"First-time privileged access to {e.ResourceType}/{e.ResourceId}"));
            }
        }

        // Rule 3 — bulk export: any event with ActionVerb == "export" and severity >= Warning.
        foreach (var e in events)
        {
            if (e.ActionVerb.Equals("export", StringComparison.OrdinalIgnoreCase) && e.Severity >= EventSeverity.Warning)
                anomalies.Add(new PrivilegedAccessAnomaly("bulk-export", e.Id, e.ActorId, e.ActorDisplayName, e.EventTime,
                    $"Privileged export: {e.ResourceType}/{e.ResourceId}"));
        }

        // Rule 4 — mass deletion: any actor with >= 10 delete events in the window.
        var deleteBursts = events.Where(e => e.ActionVerb.Equals("delete", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.ActorId).Where(g => g.Count() >= 10);
        foreach (var g in deleteBursts)
        {
            var first = g.First();
            anomalies.Add(new PrivilegedAccessAnomaly("mass-delete", first.Id, first.ActorId, first.ActorDisplayName, first.EventTime,
                $"{g.Count()} privileged deletes by {first.ActorDisplayName} in window"));
        }

        // Rule 5 — permission escalation: ActionVerb "grant" targeting role admin.
        foreach (var e in events)
        {
            if (e.ActionVerb.Equals("grant", StringComparison.OrdinalIgnoreCase) && e.ResourceType.Equals("role", StringComparison.OrdinalIgnoreCase)
                && e.ResourceName.Contains("admin", StringComparison.OrdinalIgnoreCase))
            {
                anomalies.Add(new PrivilegedAccessAnomaly("permission-escalation", e.Id, e.ActorId, e.ActorDisplayName, e.EventTime,
                    $"Privileged escalation: granted {e.ResourceName}"));
            }
        }

        return new PrivilegedAccessReport(tenantId, from, to, events.Count, anomalies);
    }
}
