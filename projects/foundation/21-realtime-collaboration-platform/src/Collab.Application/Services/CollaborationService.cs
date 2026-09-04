using System.Diagnostics;
using Collab.Application.Abstractions;
using Collab.Application.Contracts;
using Collab.Application.Options;
using Collab.Application.Serialization;
using Collab.Domain.Abstractions;
using Collab.Domain.Authorization;
using Collab.Domain.Comments;
using Collab.Domain.Crdt;
using Collab.Domain.Documents;
using Collab.Domain.Structured;
using Microsoft.Extensions.Options;

namespace Collab.Application.Services;

/// <summary>
/// The collaboration engine — the server-authoritative heart of the system. It owns the in-memory
/// <see cref="DocumentRuntime"/> per document, serializes mutations through the runtime gate to
/// assign monotonic sequence numbers, applies CRDT operations, rebases comment anchors, persists the
/// operation log and periodic snapshots, and produces the broadcast/ack/rejection the hub relays.
/// </summary>
public sealed class CollaborationService(
    IAccessControl access,
    IDocumentRepository documents,
    IOperationLogRepository opLog,
    ISnapshotRepository snapshots,
    ICommentRepository comments,
    IDocumentRuntimeCache cache,
    ICollabMetrics metrics,
    IAuditLog audit,
    IClock clock,
    IUnitOfWork uow,
    IOptions<CollaborationOptions> options)
{
    private const string ServerReplicaId = "server";
    private readonly CollaborationOptions _options = options.Value;

    // ── Runtime loading ────────────────────────────────────────────────────────────────────────

    public Task<DocumentRuntime> GetRuntimeAsync(Guid documentId, CancellationToken ct = default) =>
        cache.GetOrLoadAsync(documentId, c => LoadRuntimeAsync(documentId, c), ct);

    private async Task<DocumentRuntime> LoadRuntimeAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await documents.GetAsync(documentId, ct)
            ?? throw new NotFoundException("Document not found.");
        var snapshot = await snapshots.GetLatestAsync(documentId, ct);
        var tail = await opLog.GetSinceAsync(documentId, snapshot?.AtSequence ?? 0, ct);

        DocumentRuntime runtime;
        if (doc.Type == DocumentType.Text)
        {
            var rga = DocumentReplay.BuildText(snapshot, tail);
            runtime = new DocumentRuntime(documentId, DocumentType.Text, rga, null, doc.CurrentSequence);
            runtime.Clock.Observe(rga.MaxLamport);
        }
        else
        {
            var structured = DocumentReplay.BuildStructured(snapshot, tail);
            runtime = new DocumentRuntime(documentId, DocumentType.Structured, null, structured, doc.CurrentSequence);
            runtime.Clock.Observe(structured.MaxLamport);
        }

        runtime.OpsSinceSnapshot = tail.Count;
        return runtime;
    }

    // ── State / join ─────────────────────────────────────────────────────────────────────────────

    public async Task<DocumentStateDto> GetStateAsync(Guid userId, Guid documentId, CancellationToken ct = default)
    {
        await AuthorizeAsync(userId, documentId, Capability.View, ct);
        var runtime = await GetRuntimeAsync(documentId, ct);
        await runtime.Gate.WaitAsync(ct);
        try
        {
            return SnapshotState(runtime);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    // ── Apply (client submission) ─────────────────────────────────────────────────────────────────

    public async Task<ApplyResult> ApplyAsync(Guid userId, OperationEnvelope env, CancellationToken ct = default)
    {
        var decision = await access.ForDocumentAsync(userId, env.DocumentId, Capability.Edit, ct);
        if (!decision.Allowed)
            return Reject(env.DocumentId, "forbidden", decision.Reason, env.ClientTag);

        var total = env.TextOps.Count + env.StructuredOps.Count;
        if (total == 0)
            return Reject(env.DocumentId, "empty", "No operations submitted.", env.ClientTag);
        if (total > _options.MaxOperationsPerSubmit)
            return Reject(env.DocumentId, "too_many_operations",
                $"A submission may carry at most {_options.MaxOperationsPerSubmit} operations.", env.ClientTag);

        var doc = await documents.GetAsync(env.DocumentId, ct);
        if (doc is null)
            return Reject(env.DocumentId, "not_found", "Document not found.", env.ClientTag);

        if (doc.Type == DocumentType.Text && env.StructuredOps.Count > 0)
            return Reject(env.DocumentId, "type_mismatch", "Text document received structured operations.", env.ClientTag);
        if (doc.Type == DocumentType.Structured && env.TextOps.Count > 0)
            return Reject(env.DocumentId, "type_mismatch", "Structured document received text operations.", env.ClientTag);

        var runtime = await GetRuntimeAsync(env.DocumentId, ct);
        await runtime.Gate.WaitAsync(ct);
        try
        {
            if (doc.Type == DocumentType.Text)
            {
                var rejection = ValidateText(env, runtime);
                if (rejection is not null) return new ApplyResult(false, null, rejection);
                var ops = env.TextOps.Select(o => o.ToDomain()).ToList();
                return await CommitTextAsync(userId, runtime, doc, ops, env.ReplicaId, env.ClientTag, ct);
            }
            else
            {
                var rejection = ValidateStructured(env);
                if (rejection is not null) return new ApplyResult(false, null, rejection);
                var ops = env.StructuredOps.Select(o => o.ToDomain()).ToList();
                return await CommitStructuredAsync(userId, runtime, doc, ops, env.ReplicaId, env.ClientTag, ct);
            }
        }
        catch
        {
            // On any failure after mutating in-memory state, drop the runtime so it is rebuilt from
            // the persisted (authoritative) log on next access, keeping memory and DB consistent.
            cache.Evict(env.DocumentId);
            throw;
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private OperationRejected? ValidateText(OperationEnvelope env, DocumentRuntime runtime)
    {
        var text = runtime.Text!;
        var insertCount = env.TextOps.Count(o => o.Type == "insert");
        if (text.Length + insertCount > _options.MaxDocumentLength)
            return new OperationRejected(env.DocumentId, "document_too_large",
                $"Document would exceed the maximum of {_options.MaxDocumentLength} characters.", env.ClientTag);

        // Causal readiness: every referenced element must already exist or be created earlier in this
        // same batch. The server is authoritative and never buffers a client's op — an unresolved
        // reference is a well-defined rejection, not silent corruption.
        var known = new HashSet<ElementId>();
        foreach (var dto in env.TextOps)
        {
            if (dto.Type == "insert")
            {
                var reference = ElementId.Parse(dto.Reference);
                if (!reference.IsRoot && !text.Contains(reference) && !known.Contains(reference))
                    return new OperationRejected(env.DocumentId, "unknown_reference",
                        $"Insert references unknown element '{dto.Reference}'.", env.ClientTag);
                known.Add(ElementId.Parse(dto.Id));
            }
            else
            {
                var reference = ElementId.Parse(dto.Reference);
                if (!text.Contains(reference) && !known.Contains(reference))
                    return new OperationRejected(env.DocumentId, "unknown_reference",
                        $"Delete references unknown element '{dto.Reference}'.", env.ClientTag);
            }
        }
        return null;
    }

    private static OperationRejected? ValidateStructured(OperationEnvelope env)
    {
        foreach (var dto in env.StructuredOps)
        {
            if (string.IsNullOrWhiteSpace(dto.ItemId))
                return new OperationRejected(env.DocumentId, "invalid_operation", "Structured operation requires an item id.", env.ClientTag);
            if (dto.Type == "set" && string.IsNullOrEmpty(dto.Field))
                return new OperationRejected(env.DocumentId, "invalid_operation", "A set operation requires a field.", env.ClientTag);
        }
        return null;
    }

    // ── Commit primitives (gate already held) ─────────────────────────────────────────────────────

    private async Task<ApplyResult> CommitTextAsync(
        Guid userId, DocumentRuntime runtime, Document doc,
        IReadOnlyList<RgaOperation> ops, string replicaId, string? clientTag, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var text = runtime.Text!;

        var anchored = await comments.ListAnchoredAsync(doc.Id, ct);
        var working = anchored.Select(c => (Comment: c, Anchor: c.Anchor)).ToList();

        foreach (var op in ops)
        {
            runtime.Clock.Observe(op.Lamport);
            if (op.Type == RgaOpType.Insert)
            {
                if (text.Apply(op) == ApplyStatus.Applied)
                {
                    var offset = text.VisibleIndexOf(op.Id);
                    if (offset >= 0) RebaseAll(working, a => AnchorRebaser.OnInsert(a, offset, 1));
                }
            }
            else
            {
                var offset = text.VisibleIndexOf(op.Reference);
                if (text.Apply(op) == ApplyStatus.Applied && offset >= 0)
                    RebaseAll(working, a => AnchorRebaser.OnDelete(a, offset, 1));
            }
        }

        foreach (var (comment, anchor) in working)
            if (anchor != comment.Anchor)
                comment.UpdateAnchor(anchor, clock);

        var dtos = ops.Select(TextOpDto.From).ToList();
        var broadcast = await PersistAsync(userId, runtime, doc, replicaId, clientTag, dtos, [], ops.Count, ct);
        sw.Stop();
        metrics.RecordApply(ops.Count, sw.Elapsed.TotalMilliseconds);
        return ApplyResult.Ok(broadcast);
    }

    private async Task<ApplyResult> CommitStructuredAsync(
        Guid userId, DocumentRuntime runtime, Document doc,
        IReadOnlyList<StructuredOperation> ops, string replicaId, string? clientTag, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var structured = runtime.Structured!;
        foreach (var op in ops)
        {
            runtime.Clock.Observe(op.Stamp.Lamport);
            structured.Apply(op);
        }

        var dtos = ops.Select(StructuredOpDto.From).ToList();
        var broadcast = await PersistAsync(userId, runtime, doc, replicaId, clientTag, [], dtos, ops.Count, ct);
        sw.Stop();
        metrics.RecordApply(ops.Count, sw.Elapsed.TotalMilliseconds);
        return ApplyResult.Ok(broadcast);
    }

    private async Task<OperationBroadcast> PersistAsync(
        Guid userId, DocumentRuntime runtime, Document doc, string replicaId, string? clientTag,
        IReadOnlyList<TextOpDto> textDtos, IReadOnlyList<StructuredOpDto> structuredDtos, int opCount, CancellationToken ct)
    {
        var sequence = ++runtime.Sequence;
        var payload = new OperationPayload(replicaId, textDtos, structuredDtos);
        opLog.Add(new OperationLogEntry(doc.Id, sequence, userId, replicaId, doc.Type, CollabJson.Serialize(payload), clock));
        doc.AdvanceTo(sequence, clock);

        runtime.OpsSinceSnapshot += opCount;
        if (runtime.OpsSinceSnapshot >= _options.SnapshotEveryNOperations)
        {
            WriteSnapshot(runtime, sequence);
            runtime.OpsSinceSnapshot = 0;
        }

        await uow.SaveChangesAsync(ct);
        return new OperationBroadcast(doc.Id, sequence, userId, replicaId, textDtos, structuredDtos, clientTag);
    }

    private void WriteSnapshot(DocumentRuntime runtime, long sequence)
    {
        var (state, content) = runtime.Type == DocumentType.Text
            ? (CollabJson.Serialize(runtime.Text!.ExportState()), runtime.Text!.Materialize())
            : (CollabJson.Serialize(runtime.Structured!.ExportState()), runtime.Structured!.ToCanonicalJson());
        snapshots.Add(new DocumentSnapshot(runtime.DocumentId, sequence, state, content, clock));
    }

    // ── Resync ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<ResyncResult> ResyncAsync(Guid userId, Guid documentId, long fromSequence, CancellationToken ct = default)
    {
        await AuthorizeAsync(userId, documentId, Capability.View, ct);
        var runtime = await GetRuntimeAsync(documentId, ct);
        await runtime.Gate.WaitAsync(ct);
        try
        {
            var current = runtime.Sequence;
            var latestSnapshot = await snapshots.GetLatestAsync(documentId, ct);

            // Full resync when the client has nothing, is ahead of us, or is older than our earliest
            // replayable point (its missing tail was already compacted into a snapshot).
            var full = fromSequence <= 0
                || fromSequence > current
                || (latestSnapshot is not null && fromSequence < latestSnapshot.AtSequence);

            metrics.RecordResync();
            if (full)
                return new ResyncResult(true, SnapshotState(runtime), [], current);

            var tail = await opLog.GetSinceAsync(documentId, fromSequence, ct);
            var operations = tail.Select(ToBroadcast).ToList();
            return new ResyncResult(false, null, operations, current);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    // ── Restore (forward operation, never rewrites history) ──────────────────────────────────────

    public async Task<ApplyResult> RestoreAsync(Guid userId, Guid documentId, long toSequence, CancellationToken ct = default)
    {
        var decision = await access.ForDocumentAsync(userId, documentId, Capability.Edit, ct);
        if (!decision.Allowed)
            return Reject(documentId, "forbidden", decision.Reason, null);

        var doc = await documents.GetAsync(documentId, ct);
        if (doc is null)
            return Reject(documentId, "not_found", "Document not found.", null);
        if (toSequence < 0 || toSequence > doc.CurrentSequence)
            return Reject(documentId, "invalid_sequence", "Target sequence is out of range.", null);

        var snapshot = await snapshots.GetLatestAtOrBeforeAsync(documentId, toSequence, ct);
        var baseSequence = snapshot?.AtSequence ?? 0;
        var tail = (await opLog.GetUpToAsync(documentId, toSequence, ct))
            .Where(e => e.ServerSequence > baseSequence)
            .ToList();

        var runtime = await GetRuntimeAsync(documentId, ct);
        await runtime.Gate.WaitAsync(ct);
        try
        {
            ApplyResult result;
            if (doc.Type == DocumentType.Text)
            {
                var target = DocumentReplay.RenderText(snapshot, tail);
                var ops = BuildRestoreTextOps(runtime, target);
                if (ops.Count == 0)
                    return Reject(documentId, "no_changes", "Document already matches the target version.", null);
                audit.Record("document.restored", "document", documentId.ToString(), userId, doc.WorkspaceId, documentId,
                    details: $"toSequence={toSequence}");
                result = await CommitTextAsync(userId, runtime, doc, ops, ServerReplicaId, null, ct);
            }
            else
            {
                var target = DocumentReplay.BuildStructured(snapshot, tail);
                var ops = BuildRestoreStructuredOps(runtime, target);
                if (ops.Count == 0)
                    return Reject(documentId, "no_changes", "Document already matches the target version.", null);
                audit.Record("document.restored", "document", documentId.ToString(), userId, doc.WorkspaceId, documentId,
                    details: $"toSequence={toSequence}");
                result = await CommitStructuredAsync(userId, runtime, doc, ops, ServerReplicaId, null, ct);
            }
            return result;
        }
        catch
        {
            cache.Evict(documentId);
            throw;
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>
    /// Translate a char-level diff (current → target) into RGA operations. Deletions tombstone the
    /// original elements; insertions chain new elements after the last surviving element, so
    /// surviving text keeps its identity and comment anchors on it are preserved.
    /// </summary>
    private static IReadOnlyList<RgaOperation> BuildRestoreTextOps(DocumentRuntime runtime, string target)
    {
        var text = runtime.Text!;
        var current = text.Materialize();
        if (string.Equals(current, target, StringComparison.Ordinal))
            return [];

        var visible = text.VisibleElementIds();
        var ops = new List<RgaOperation>();
        var pos = 0;
        var lastParent = ElementId.Root;

        foreach (var segment in Collab.Domain.Diffing.DiffEngine.DiffChars(current, target))
        {
            switch (segment.Kind)
            {
                case Collab.Domain.Diffing.DiffKind.Equal:
                    foreach (var _ in segment.Text)
                    {
                        lastParent = visible[pos];
                        pos++;
                    }
                    break;
                case Collab.Domain.Diffing.DiffKind.Delete:
                    foreach (var _ in segment.Text)
                    {
                        ops.Add(RgaOperation.Delete(visible[pos]));
                        pos++;
                    }
                    break;
                case Collab.Domain.Diffing.DiffKind.Insert:
                    foreach (var ch in segment.Text)
                    {
                        var id = new ElementId(runtime.Clock.Tick(), ServerReplicaId);
                        ops.Add(RgaOperation.Insert(id, lastParent, ch));
                        lastParent = id;
                    }
                    break;
            }
        }
        return ops;
    }

    private static IReadOnlyList<StructuredOperation> BuildRestoreStructuredOps(DocumentRuntime runtime, StructuredDocument target)
    {
        var current = runtime.Structured!;
        var ops = new List<StructuredOperation>();
        LwwStamp Stamp() => new(runtime.Clock.Tick(), ServerReplicaId);

        foreach (var item in target.ExportState().Items)
        {
            if (!target.HasItem(item.Id)) continue;
            if (!current.HasItem(item.Id))
                ops.Add(StructuredOperation.AddItem(item.Id, Stamp()));
            foreach (var field in item.Fields)
                if (current.GetField(item.Id, field.Field) != field.Value)
                    ops.Add(StructuredOperation.SetField(item.Id, field.Field, field.Value, Stamp()));
        }

        foreach (var id in current.PresentItemIds)
            if (!target.HasItem(id))
                ops.Add(StructuredOperation.RemoveItem(id, Stamp()));

        return ops;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static DocumentStateDto SnapshotState(DocumentRuntime runtime) =>
        runtime.Type == DocumentType.Text
            ? new DocumentStateDto(runtime.DocumentId, runtime.Type, runtime.Sequence,
                CollabJson.Serialize(runtime.Text!.ExportState()), runtime.Text!.Materialize())
            : new DocumentStateDto(runtime.DocumentId, runtime.Type, runtime.Sequence,
                CollabJson.Serialize(runtime.Structured!.ExportState()), runtime.Structured!.ToCanonicalJson());

    private static OperationBroadcast ToBroadcast(OperationLogEntry entry)
    {
        var payload = CollabJson.Deserialize<OperationPayload>(entry.Payload);
        return new OperationBroadcast(entry.DocumentId, entry.ServerSequence, entry.AuthorUserId,
            entry.AuthorReplicaId, payload.TextOps, payload.StructuredOps, null);
    }

    private static void RebaseAll(List<(Comment Comment, TextAnchor Anchor)> working, Func<TextAnchor, TextAnchor> rebase)
    {
        for (var i = 0; i < working.Count; i++)
            working[i] = (working[i].Comment, rebase(working[i].Anchor));
    }

    private async Task AuthorizeAsync(Guid userId, Guid documentId, Capability capability, CancellationToken ct)
    {
        var decision = await access.ForDocumentAsync(userId, documentId, capability, ct);
        if (!decision.Allowed) throw new ForbiddenException(decision.Reason);
    }

    private ApplyResult Reject(Guid documentId, string code, string reason, string? clientTag)
    {
        metrics.RecordRejected();
        return ApplyResult.Reject(documentId, code, reason, clientTag);
    }
}
