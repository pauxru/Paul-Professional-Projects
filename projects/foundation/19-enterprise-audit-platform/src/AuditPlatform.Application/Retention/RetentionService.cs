using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Events;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Ids;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Time;

namespace AuditPlatform.Application.Retention;

public sealed record PruneReport(int Considered, int Pruned, int LegalHoldSkips);

/// <summary>
/// Retention pruner. Applies per-tenant, per-category retention policies while honouring legal
/// holds and preserving chain verifiability. Pruning converts events to tombstones — the
/// ContentHash is retained on the tombstone, and the chain link continues to verify with
/// <see cref="HashChain.LinkHash"/> using the preserved ContentHash.
/// </summary>
public sealed class RetentionService
{
    private readonly IAuditEventStore _events;
    private readonly IRetentionStore _policies;
    private readonly ILegalHoldStore _holds;
    private readonly IClock _clock;
    private readonly IAuditSelfLogger _selfLog;
    private readonly IIdGenerator _ids;

    public RetentionService(IAuditEventStore events, IRetentionStore policies, ILegalHoldStore holds,
        IClock clock, IAuditSelfLogger selfLog, IIdGenerator ids)
    {
        _events = events;
        _policies = policies;
        _holds = holds;
        _clock = clock;
        _selfLog = selfLog;
        _ids = ids;
    }

    public async Task<PruneReport> RunAsync(string tenantId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var policies = await _policies.ListAsync(tenantId, ct);
        var holds = await _holds.ListActiveAsync(tenantId, ct);
        var holdSet = holds.Select(h => (h.ResourceType, h.ResourceId)).ToHashSet();

        int considered = 0, pruned = 0, holdSkips = 0;

        foreach (var policy in policies)
        {
            var cutoff = now.AddDays(-policy.RetainForDays);
            IQueryable<AuditEvent> q = _events.Query(tenantId).Where(e => e.EventTime < cutoff && !e.IsTombstoned);
            if (policy.CategoryPattern != "*" && Enum.TryParse<EventCategory>(policy.CategoryPattern, ignoreCase: true, out var cat))
                q = q.Where(e => e.Category == cat);

            var candidates = q.ToList();
            considered += candidates.Count;
            foreach (var evt in candidates)
            {
                if (holdSet.Contains((evt.ResourceType, evt.ResourceId)))
                {
                    holdSkips++;
                    continue;
                }
                evt.Tombstone(now);
                pruned++;
            }
        }

        await _events.SaveChangesAsync(ct);

        await _selfLog.LogAsync(new IngestEventRequest(
            TenantId: tenantId,
            EventType: "audit.retention.pruned",
            SchemaVersion: 1,
            EventTime: now,
            Actor: new ActorInput(ActorType.System, "retention-service", "Retention Service", new List<string> { "audit:admin" }),
            ActionVerb: "prune",
            Category: EventCategory.Retention,
            Resource: new ResourceInput("tenant", tenantId, tenantId, "/"),
            Outcome: EventOutcome.Success,
            Severity: EventSeverity.Info,
            Source: new SourceInput("127.0.0.1", "retention-service", "audit-platform", "local"),
            CorrelationId: Guid.NewGuid().ToString("n"),
            CausationId: null,
            TraceId: null,
            ClientEventId: null,
            Data: System.Text.Json.JsonDocument.Parse($"{{\"considered\":{considered},\"pruned\":{pruned},\"legalHoldSkips\":{holdSkips}}}").RootElement,
            Before: null,
            After: null), ct);

        return new PruneReport(considered, pruned, holdSkips);
    }
}

public interface IAuditSelfLogger
{
    Task LogAsync(IngestEventRequest request, CancellationToken ct);
}
