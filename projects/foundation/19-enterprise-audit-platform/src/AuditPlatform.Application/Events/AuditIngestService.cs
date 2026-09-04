using System.Text;
using System.Text.Json;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Domain.Errors;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Ids;
using AuditPlatform.Domain.Integrity;
using AuditPlatform.Domain.Schemas;
using AuditPlatform.Domain.Serialization;
using AuditPlatform.Domain.Time;

namespace AuditPlatform.Application.Events;

public sealed class IngestOptions
{
    public bool RequireSchemaMatch { get; set; } = true;
}

/// <summary>
/// The append path. All events flow through this class so that the hash-chain invariant is
/// enforced in exactly one place. Each call:
///   1. Validates the input against the registered schema (if any).
///   2. Looks up the tenant's current chain tip.
///   3. Serialises the event payload canonically.
///   4. Computes ContentHash and ChainHash.
///   5. Appends the new event and returns the new chain hash to the caller.
/// The tenant lock is required because a hash chain is by definition a serialised structure.
/// </summary>
public sealed class AuditIngestService
{
    private readonly IAuditEventStore _events;
    private readonly ISchemaRegistry _schemas;
    private readonly IDeadLetterStore _deadLetter;
    private readonly ISearchIndex _search;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly ITenantLock _tenantLock;
    private readonly IngestOptions _options;

    public AuditIngestService(
        IAuditEventStore events,
        ISchemaRegistry schemas,
        IDeadLetterStore deadLetter,
        ISearchIndex search,
        IIdGenerator ids,
        IClock clock,
        ITenantLock tenantLock,
        IngestOptions options)
    {
        _events = events;
        _schemas = schemas;
        _deadLetter = deadLetter;
        _search = search;
        _ids = ids;
        _clock = clock;
        _tenantLock = tenantLock;
        _options = options;
    }

    public async Task<IngestResult> IngestAsync(IngestEventRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            return Reject(DomainErrorCode.TenantMissing, "tenantId is required");
        if (string.IsNullOrWhiteSpace(request.EventType))
            return Reject(DomainErrorCode.SchemaValidationFailed, "eventType is required");

        // Idempotent replay: if the caller supplied a clientEventId that we've already accepted,
        // return the original acceptance verbatim.
        if (!string.IsNullOrWhiteSpace(request.ClientEventId))
        {
            var existing = await _events.GetByClientEventIdAsync(request.TenantId, request.ClientEventId!, ct);
            if (existing is not null)
            {
                return new IngestResult(true, existing.Id, existing.SequenceNumber, existing.ChainHash, "duplicate accepted", WasDuplicate: true);
            }
        }

        var schema = await _schemas.GetAsync(request.EventType, request.SchemaVersion, ct);
        if (schema is null && _options.RequireSchemaMatch)
        {
            var raw = SerializeRequest(request);
            await _deadLetter.AddAsync(DeadLetterEvent.Record(_ids.NewId(), request.TenantId, raw, "unknown schema", _clock.UtcNow), ct);
            await _deadLetter.SaveChangesAsync(ct);
            return Reject(DomainErrorCode.UnknownSchema, $"Unknown schema '{request.EventType}' v{request.SchemaVersion}");
        }
        if (schema is not null)
        {
            var def = SchemaDefinition.Parse(schema.SchemaJson);
            var errors = def.Validate(request.Data).ToList();
            if (errors.Count > 0)
            {
                var raw = SerializeRequest(request);
                await _deadLetter.AddAsync(DeadLetterEvent.Record(_ids.NewId(), request.TenantId, raw, string.Join("; ", errors), _clock.UtcNow), ct);
                await _deadLetter.SaveChangesAsync(ct);
                return Reject(DomainErrorCode.SchemaValidationFailed, string.Join("; ", errors));
            }
        }

        // A tenant-scoped lock keeps chain writes strictly serial so that ChainHash always builds
        // on the correct tip. In an in-process test host this is a SemaphoreSlim; a multi-node
        // deployment would use a leased-row / advisory lock — documented in ADR-005.
        await using var _ = await _tenantLock.AcquireAsync(request.TenantId, ct);

        var tip = await _events.GetLatestForTenantAsync(request.TenantId, ct);
        var previousChainHash = tip?.ChainHash ?? HashChain.GenesisHash(request.TenantId);
        var nextSequence = (tip?.SequenceNumber ?? 0) + 1;

        var payloadJson = BuildPayloadJson(request);
        var canonical = CanonicalJson.Serialize(payloadJson);
        var contentHash = HashChain.ContentHash(canonical);
        var chainHash = HashChain.LinkHash(contentHash, previousChainHash);

        var ingestTime = _clock.UtcNow;
        var id = _ids.NewId(ingestTime);

        var beforeHash = HashOrNull(request.Before);
        var afterHash = HashOrNull(request.After);

        var evt = AuditEvent.CreateInternal(
            id: id,
            tenantId: request.TenantId,
            eventType: request.EventType,
            schemaVersion: request.SchemaVersion,
            eventTime: request.EventTime,
            ingestTime: ingestTime,
            actorType: request.Actor.Type,
            actorId: request.Actor.Id,
            actorDisplayName: request.Actor.DisplayName,
            actorRolesCsv: string.Join(',', request.Actor.Roles ?? new List<string>()),
            actionVerb: request.ActionVerb,
            category: request.Category,
            resourceType: request.Resource.Type,
            resourceId: request.Resource.Id,
            resourceName: request.Resource.Name,
            resourceParentPath: request.Resource.ParentPath,
            outcome: request.Outcome,
            severity: request.Severity,
            sourceIp: request.Source.Ip,
            sourceUserAgent: request.Source.UserAgent,
            sourceService: request.Source.Service,
            sourceRegion: request.Source.Region,
            correlationId: request.CorrelationId,
            causationId: request.CausationId,
            traceId: request.TraceId ?? request.CorrelationId,
            clientEventId: request.ClientEventId,
            payloadJson: Encoding.UTF8.GetString(canonical),
            contentHash: contentHash,
            chainHash: chainHash,
            previousChainHash: previousChainHash,
            sequenceNumber: nextSequence,
            beforeHash: beforeHash,
            afterHash: afterHash);

        await _events.AppendAsync(evt, ct);
        await _events.SaveChangesAsync(ct);
        await _search.IndexAsync(evt.Id, request.TenantId, BuildIndexText(evt, request), ct);

        return new IngestResult(true, evt.Id, evt.SequenceNumber, evt.ChainHash, null, WasDuplicate: false);
    }

    public async Task<IReadOnlyList<IngestResult>> IngestBatchAsync(IReadOnlyList<IngestEventRequest> batch, CancellationToken ct)
    {
        var results = new List<IngestResult>(batch.Count);
        foreach (var req in batch)
        {
            // Deliberately continue-on-error: partial-failure semantics let the caller inspect
            // per-event outcomes. Failed rows land in the dead-letter store.
            var r = await IngestAsync(req, ct);
            results.Add(r);
        }
        return results;
    }

    private static IngestResult Reject(DomainErrorCode code, string reason) => new(false, null, null, null, $"{code}:{reason}", WasDuplicate: false);

    private static string BuildPayloadJson(IngestEventRequest r)
    {
        // The canonical payload is what enters the hash chain. We include *every* field that
        // uniquely identifies this event, plus a nested "data" element carrying the caller's
        // domain-specific payload.
        var writer = new StringBuilder();
        using var jw = new Utf8JsonWriter(new IoWriter(writer));
        jw.WriteStartObject();
        jw.WriteString("tenantId", r.TenantId);
        jw.WriteString("eventType", r.EventType);
        jw.WriteNumber("schemaVersion", r.SchemaVersion);
        jw.WriteString("eventTime", r.EventTime.UtcDateTime.ToString("o"));
        jw.WriteString("actionVerb", r.ActionVerb);
        jw.WriteNumber("category", (int)r.Category);
        jw.WriteNumber("outcome", (int)r.Outcome);
        jw.WriteNumber("severity", (int)r.Severity);
        jw.WriteString("correlationId", r.CorrelationId ?? string.Empty);
        if (r.CausationId is not null) jw.WriteString("causationId", r.CausationId);
        if (!string.IsNullOrEmpty(r.TraceId)) jw.WriteString("traceId", r.TraceId);
        if (r.ClientEventId is not null) jw.WriteString("clientEventId", r.ClientEventId);

        jw.WriteStartObject("actor");
        jw.WriteNumber("type", (int)r.Actor.Type);
        jw.WriteString("id", r.Actor.Id);
        jw.WriteString("displayName", r.Actor.DisplayName);
        jw.WriteStartArray("roles");
        foreach (var role in r.Actor.Roles ?? new List<string>()) jw.WriteStringValue(role);
        jw.WriteEndArray();
        jw.WriteEndObject();

        jw.WriteStartObject("resource");
        jw.WriteString("type", r.Resource.Type);
        jw.WriteString("id", r.Resource.Id);
        jw.WriteString("name", r.Resource.Name);
        jw.WriteString("parentPath", r.Resource.ParentPath);
        jw.WriteEndObject();

        jw.WriteStartObject("source");
        jw.WriteString("ip", r.Source.Ip);
        jw.WriteString("userAgent", r.Source.UserAgent);
        jw.WriteString("service", r.Source.Service);
        jw.WriteString("region", r.Source.Region);
        jw.WriteEndObject();

        jw.WritePropertyName("data");
        r.Data.WriteTo(jw);
        if (r.Before is JsonElement b)
        {
            jw.WritePropertyName("before");
            b.WriteTo(jw);
        }
        if (r.After is JsonElement a)
        {
            jw.WritePropertyName("after");
            a.WriteTo(jw);
        }
        jw.WriteEndObject();
        jw.Flush();
        return writer.ToString();
    }

    private static string? HashOrNull(JsonElement? state)
    {
        if (state is null) return null;
        var canonical = CanonicalJson.Serialize(state.Value);
        return HashChain.ContentHash(canonical);
    }

    private static string BuildIndexText(AuditEvent evt, IngestEventRequest req)
    {
        var sb = new StringBuilder();
        sb.Append(evt.EventType).Append(' ')
          .Append(evt.ActionVerb).Append(' ')
          .Append(evt.ActorDisplayName).Append(' ')
          .Append(evt.ActorId).Append(' ')
          .Append(evt.ResourceType).Append(' ')
          .Append(evt.ResourceId).Append(' ')
          .Append(evt.ResourceName).Append(' ')
          .Append(evt.CorrelationId);
        if (req.Data.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in req.Data.EnumerateObject())
            {
                sb.Append(' ').Append(p.Name).Append('=').Append(p.Value);
            }
        }
        return sb.ToString();
    }

    private static string SerializeRequest(IngestEventRequest r)
        => JsonSerializer.Serialize(new
        {
            tenantId = r.TenantId,
            eventType = r.EventType,
            r.SchemaVersion,
            r.EventTime,
            r.Actor,
            r.ActionVerb,
            r.Category,
            r.Resource,
            r.Outcome,
            r.Severity,
            r.Source,
            r.CorrelationId,
            r.CausationId,
            r.TraceId,
            r.ClientEventId
        });

    private sealed class IoWriter : System.IO.Stream
    {
        private readonly StringBuilder _sb;
        public IoWriter(StringBuilder sb) => _sb = sb;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _sb.Length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _sb.Append(Encoding.UTF8.GetString(buffer, offset, count));
    }
}

/// <summary>Per-tenant serialisation of chain writes. In-process by default.</summary>
public interface ITenantLock
{
    Task<IAsyncDisposable> AcquireAsync(string tenantId, CancellationToken ct);
}
