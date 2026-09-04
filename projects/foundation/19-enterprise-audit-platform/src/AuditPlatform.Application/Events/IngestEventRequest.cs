using AuditPlatform.Domain.Events;

namespace AuditPlatform.Application.Events;

/// <summary>
/// External input shape for a new event. Kept explicit so callers, HTTP handlers and the sink
/// library all agree on the field set.
/// </summary>
public sealed record IngestEventRequest(
    string TenantId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset EventTime,
    ActorInput Actor,
    string ActionVerb,
    EventCategory Category,
    ResourceInput Resource,
    EventOutcome Outcome,
    EventSeverity Severity,
    SourceInput Source,
    string CorrelationId,
    string? CausationId,
    string? TraceId,
    string? ClientEventId,
    System.Text.Json.JsonElement Data,
    System.Text.Json.JsonElement? Before,
    System.Text.Json.JsonElement? After
);

public sealed record ActorInput(ActorType Type, string Id, string DisplayName, IReadOnlyList<string> Roles);
public sealed record ResourceInput(string Type, string Id, string Name, string ParentPath);
public sealed record SourceInput(string Ip, string UserAgent, string Service, string Region);

public sealed record IngestResult(
    bool Accepted,
    Guid? EventId,
    long? SequenceNumber,
    string? ChainHash,
    string? Reason,
    bool WasDuplicate
);

public sealed record EventDto(
    Guid Id,
    string TenantId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset EventTime,
    DateTimeOffset IngestTime,
    string ActorId,
    string ActorDisplayName,
    ActorType ActorType,
    IReadOnlyList<string> ActorRoles,
    string ActionVerb,
    EventCategory Category,
    string ResourceType,
    string ResourceId,
    string ResourceName,
    string ResourceParentPath,
    EventOutcome Outcome,
    EventSeverity Severity,
    string SourceIp,
    string SourceUserAgent,
    string SourceService,
    string SourceRegion,
    string CorrelationId,
    string? CausationId,
    string TraceId,
    string? ClientEventId,
    string ContentHash,
    string ChainHash,
    string PreviousChainHash,
    long SequenceNumber,
    string? BeforeHash,
    string? AfterHash,
    bool IsTombstoned,
    System.Text.Json.JsonElement Payload
);

public static class EventMapper
{
    public static EventDto ToDto(AuditEvent evt)
    {
        var payload = string.IsNullOrWhiteSpace(evt.PayloadJson)
            ? System.Text.Json.JsonDocument.Parse("{}").RootElement
            : System.Text.Json.JsonDocument.Parse(evt.PayloadJson).RootElement;
        var roles = string.IsNullOrEmpty(evt.ActorRolesCsv) ? Array.Empty<string>() : evt.ActorRolesCsv.Split(',');
        return new EventDto(
            evt.Id, evt.TenantId, evt.EventType, evt.SchemaVersion, evt.EventTime, evt.IngestTime,
            evt.ActorId, evt.ActorDisplayName, evt.ActorType, roles,
            evt.ActionVerb, evt.Category,
            evt.ResourceType, evt.ResourceId, evt.ResourceName, evt.ResourceParentPath,
            evt.Outcome, evt.Severity,
            evt.SourceIp, evt.SourceUserAgent, evt.SourceService, evt.SourceRegion,
            evt.CorrelationId, evt.CausationId, evt.TraceId, evt.ClientEventId,
            evt.ContentHash, evt.ChainHash, evt.PreviousChainHash, evt.SequenceNumber,
            evt.BeforeHash, evt.AfterHash, evt.IsTombstoned, payload);
    }
}
