using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Query;
using AuditPlatform.Application.Security;
using AuditPlatform.Application.Verification;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;

namespace AuditPlatform.Application.Exports;

public sealed record ExportManifest(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    int EventCount,
    int CheckpointCount,
    string EventsSha256,
    string CheckpointsSha256,
    string SigningKeyId,
    string SignatureBase64,
    DateTimeOffset GeneratedAt);

public sealed class ExportService
{
    private readonly IAuditEventStore _events;
    private readonly ICheckpointStore _checkpoints;
    private readonly ISigningService _signing;

    public ExportService(IAuditEventStore events, ICheckpointStore checkpoints, ISigningService signing)
    {
        _events = events;
        _checkpoints = checkpoints;
        _signing = signing;
    }

    public async IAsyncEnumerable<string> ExportNdjsonAsync(EventFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var query = _events.Query(filter.TenantId).AsQueryable();
        if (filter.From is { } from) query = query.Where(e => e.EventTime >= from);
        if (filter.To is { } to) query = query.Where(e => e.EventTime <= to);
        query = query.OrderBy(e => e.SequenceNumber);
        foreach (var evt in query)
        {
            ct.ThrowIfCancellationRequested();
            var dto = Application.Events.EventMapper.ToDto(evt);
            yield return System.Text.Json.JsonSerializer.Serialize(dto);
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ExportCsvAsync(EventFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return "id,tenantId,sequence,eventTime,actorId,actionVerb,resourceType,resourceId,outcome,severity,chainHash";
        var query = _events.Query(filter.TenantId).AsQueryable();
        if (filter.From is { } from) query = query.Where(e => e.EventTime >= from);
        if (filter.To is { } to) query = query.Where(e => e.EventTime <= to);
        query = query.OrderBy(e => e.SequenceNumber);
        foreach (var evt in query)
        {
            ct.ThrowIfCancellationRequested();
            yield return string.Join(',', new[]
            {
                evt.Id.ToString(),
                CsvEscape(evt.TenantId),
                evt.SequenceNumber.ToString(),
                evt.EventTime.UtcDateTime.ToString("o"),
                CsvEscape(evt.ActorId),
                CsvEscape(evt.ActionVerb),
                CsvEscape(evt.ResourceType),
                CsvEscape(evt.ResourceId),
                evt.Outcome.ToString(),
                evt.Severity.ToString(),
                evt.ChainHash
            });
        }
        await Task.CompletedTask;
    }

    private static string CsvEscape(string v)
    {
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    public async Task<EvidencePack> BuildEvidencePackAsync(string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var events = _events.Query(tenantId)
            .Where(e => e.EventTime >= from && e.EventTime <= to)
            .OrderBy(e => e.SequenceNumber)
            .ToList();
        var checkpoints = await _checkpoints.ListForTenantAsync(tenantId, ct);
        var overlappingCheckpoints = checkpoints
            .Where(c => events.Count == 0 || (c.ToSequence >= events[0].SequenceNumber && c.FromSequence <= events[^1].SequenceNumber))
            .ToList();

        var eventDtos = events.Select(Application.Events.EventMapper.ToDto).ToList();
        var eventsJson = System.Text.Json.JsonSerializer.Serialize(eventDtos);
        var checkpointsJson = System.Text.Json.JsonSerializer.Serialize(overlappingCheckpoints.Select(c => new
        {
            c.Id, c.TenantId, c.FromSequence, c.ToSequence, c.MerkleRoot, c.FirstChainHash, c.LastChainHash, c.LeafCount, c.CreatedAt, c.SignatureBase64, c.SigningKeyId
        }));

        var eventsHash = HashChain.Hex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(eventsJson)));
        var checkpointsHash = HashChain.Hex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(checkpointsJson)));

        var manifest = new ExportManifest(tenantId, from, to, eventDtos.Count, overlappingCheckpoints.Count,
            eventsHash, checkpointsHash, _signing.KeyId, string.Empty, DateTimeOffset.UtcNow);

        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(
            $"{manifest.TenantId}|{manifest.From:o}|{manifest.To:o}|{manifest.EventCount}|{manifest.CheckpointCount}|{manifest.EventsSha256}|{manifest.CheckpointsSha256}");
        var manifestSig = _signing.SignBase64(manifestBytes);
        manifest = manifest with { SignatureBase64 = manifestSig };

        return new EvidencePack(manifest, eventsJson, checkpointsJson);
    }

    public bool VerifyEvidencePack(EvidencePack pack)
    {
        var eventsHash = HashChain.Hex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pack.EventsJson)));
        var checkpointsHash = HashChain.Hex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pack.CheckpointsJson)));
        if (eventsHash != pack.Manifest.EventsSha256 || checkpointsHash != pack.Manifest.CheckpointsSha256) return false;

        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(
            $"{pack.Manifest.TenantId}|{pack.Manifest.From:o}|{pack.Manifest.To:o}|{pack.Manifest.EventCount}|{pack.Manifest.CheckpointCount}|{pack.Manifest.EventsSha256}|{pack.Manifest.CheckpointsSha256}");
        return _signing.Verify(manifestBytes, pack.Manifest.SignatureBase64);
    }
}

public sealed record EvidencePack(ExportManifest Manifest, string EventsJson, string CheckpointsJson);
