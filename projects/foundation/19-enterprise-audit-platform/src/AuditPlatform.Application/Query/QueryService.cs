using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Events;
using AuditPlatform.Application.Retention;
using AuditPlatform.Application.Security;
using AuditPlatform.Domain.Events;

namespace AuditPlatform.Application.Query;

public sealed record EventFilter(
    string TenantId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? ActorId,
    string? ActionVerb,
    string? ResourceType,
    string? ResourceId,
    EventOutcome? Outcome,
    EventSeverity? Severity,
    EventCategory? Category,
    string? CorrelationId,
    string? SearchText,
    int PageSize,
    string? Cursor
);

public sealed record EventPage(IReadOnlyList<EventDto> Items, string? NextCursor, int PageSize);

public sealed record ActorAggregate(string ActorId, string ActorDisplayName, int Count);
public sealed record ActionAggregate(string ActionVerb, int Count);
public sealed record DailyAggregate(string Day, int Count);

public sealed class QueryService
{
    private readonly IAuditEventStore _events;
    private readonly ISearchIndex _search;
    private readonly RedactionService _redaction;
    private readonly IAuditSelfLogger _selfLog;

    public QueryService(IAuditEventStore events, ISearchIndex search, RedactionService redaction, IAuditSelfLogger selfLog)
    {
        _events = events;
        _search = search;
        _redaction = redaction;
        _selfLog = selfLog;
    }

    public async Task<EventPage> QueryAsync(EventFilter filter, ReaderContext reader, CancellationToken ct)
    {
        var pageSize = Math.Clamp(filter.PageSize, 1, 500);
        var query = _events.Query(filter.TenantId);
        if (filter.From is { } from) query = query.Where(e => e.EventTime >= from);
        if (filter.To is { } to) query = query.Where(e => e.EventTime <= to);
        if (!string.IsNullOrWhiteSpace(filter.ActorId)) query = query.Where(e => e.ActorId == filter.ActorId);
        if (!string.IsNullOrWhiteSpace(filter.ActionVerb)) query = query.Where(e => e.ActionVerb == filter.ActionVerb);
        if (!string.IsNullOrWhiteSpace(filter.ResourceType)) query = query.Where(e => e.ResourceType == filter.ResourceType);
        if (!string.IsNullOrWhiteSpace(filter.ResourceId)) query = query.Where(e => e.ResourceId == filter.ResourceId);
        if (filter.Outcome is { } oc) query = query.Where(e => e.Outcome == oc);
        if (filter.Severity is { } sv) query = query.Where(e => e.Severity == sv);
        if (filter.Category is { } cat) query = query.Where(e => e.Category == cat);
        if (!string.IsNullOrWhiteSpace(filter.CorrelationId)) query = query.Where(e => e.CorrelationId == filter.CorrelationId);

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var ids = await _search.SearchAsync(filter.TenantId, filter.SearchText!, pageSize * 10, ct);
            var idSet = ids.ToHashSet();
            query = query.Where(e => idSet.Contains(e.Id));
        }

        // Keyset (cursor) pagination. Cursor encodes the last seen SequenceNumber; the base query
        // is ordered by SequenceNumber ascending, so a cursor uniquely identifies the resume point
        // even if new events land while a page is being consumed.
        long afterSeq = 0;
        if (!string.IsNullOrWhiteSpace(filter.Cursor) && long.TryParse(filter.Cursor, out var parsed)) afterSeq = parsed;
        query = query.Where(e => e.SequenceNumber > afterSeq).OrderBy(e => e.SequenceNumber).Take(pageSize + 1);

        var buffered = query.ToList();
        string? next = null;
        if (buffered.Count > pageSize)
        {
            next = buffered[pageSize - 1].SequenceNumber.ToString();
            buffered = buffered.Take(pageSize).ToList();
        }

        // Log the read as a meta-audit event, unless the reader itself is the meta-audit writer
        // (loop prevention).
        if (!reader.IsMetaAuditor)
        {
            await _selfLog.LogAsync(new IngestEventRequest(
                TenantId: filter.TenantId,
                EventType: "audit.log.read",
                SchemaVersion: 1,
                EventTime: DateTimeOffset.UtcNow,
                Actor: new ActorInput(reader.ActorType, reader.ActorId, reader.ActorDisplayName, reader.Scopes.ToList()),
                ActionVerb: "read",
                Category: EventCategory.MetaAudit,
                Resource: new ResourceInput("audit-log", filter.TenantId, filter.TenantId, "/"),
                Outcome: EventOutcome.Success,
                Severity: EventSeverity.Info,
                Source: new SourceInput(reader.SourceIp, reader.UserAgent, "audit-platform", "local"),
                CorrelationId: reader.CorrelationId,
                CausationId: null,
                TraceId: null,
                ClientEventId: null,
                Data: System.Text.Json.JsonDocument.Parse($"{{\"itemsReturned\":{buffered.Count}}}").RootElement,
                Before: null,
                After: null), ct);
        }

        var dtos = buffered.Select(EventMapper.ToDto).Select(dto => _redaction.Redact(dto, reader)).ToList();
        return new EventPage(dtos, next, pageSize);
    }

    public IReadOnlyList<ActorAggregate> AggregateByActor(string tenantId, DateTimeOffset from, DateTimeOffset to)
    {
        return _events.Query(tenantId).Where(e => e.EventTime >= from && e.EventTime <= to)
            .GroupBy(e => new { e.ActorId, e.ActorDisplayName })
            .Select(g => new ActorAggregate(g.Key.ActorId, g.Key.ActorDisplayName, g.Count()))
            .OrderByDescending(g => g.Count).Take(50).ToList();
    }

    public IReadOnlyList<ActionAggregate> AggregateByAction(string tenantId, DateTimeOffset from, DateTimeOffset to)
    {
        return _events.Query(tenantId).Where(e => e.EventTime >= from && e.EventTime <= to)
            .GroupBy(e => e.ActionVerb)
            .Select(g => new ActionAggregate(g.Key, g.Count()))
            .OrderByDescending(g => g.Count).Take(50).ToList();
    }

    public IReadOnlyList<DailyAggregate> AggregateByDay(string tenantId, DateTimeOffset from, DateTimeOffset to)
    {
        var events = _events.Query(tenantId).Where(e => e.EventTime >= from && e.EventTime <= to).ToList();
        return events
            .GroupBy(e => e.EventTime.UtcDateTime.Date)
            .Select(g => new DailyAggregate(g.Key.ToString("yyyy-MM-dd"), g.Count()))
            .OrderBy(g => g.Day)
            .ToList();
    }
}
