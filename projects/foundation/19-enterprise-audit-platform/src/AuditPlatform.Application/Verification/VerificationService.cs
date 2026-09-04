using AuditPlatform.Application.Abstractions;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;
using AuditPlatform.Domain.Serialization;

namespace AuditPlatform.Application.Verification;

public sealed record ChainVerificationReport(
    string TenantId,
    long FromSequence,
    long ToSequence,
    long ChecksPerformed,
    bool IsValid,
    long? BrokenAtSequence,
    Guid? BrokenAtEventId,
    string? Reason);

/// <summary>
/// Chain verification. Walks the events for a tenant (or a sub-range), recomputes each link,
/// and reports the first sequence at which any mismatch is detected. Tombstoned events are
/// verified via <see cref="HashChain.LinkHash"/> using the preserved ContentHash so pruning does not break the chain.
/// </summary>
public sealed class VerificationService
{
    private readonly IAuditEventStore _events;
    private readonly ICheckpointStore _checkpoints;
    private readonly ISigningService _signing;

    public VerificationService(IAuditEventStore events, ICheckpointStore checkpoints, ISigningService signing)
    {
        _events = events;
        _checkpoints = checkpoints;
        _signing = signing;
    }

    public async Task<ChainVerificationReport> VerifyAsync(string tenantId, long fromSequence, long toSequence, CancellationToken ct)
    {
        var events = await _events.ListForVerificationAsync(tenantId, fromSequence, toSequence, ct);
        if (events.Count == 0)
            return new ChainVerificationReport(tenantId, fromSequence, toSequence, 0, true, null, null, "no events in range");

        // Determine the expected previous hash for the first event we look at.
        string expectedPreviousHash;
        var first = events[0];
        if (first.SequenceNumber == 1)
        {
            expectedPreviousHash = HashChain.GenesisHash(tenantId);
        }
        else
        {
            // Ask the store for the immediately-preceding event.
            var prior = await _events.ListForVerificationAsync(tenantId, first.SequenceNumber - 1, first.SequenceNumber - 1, ct);
            if (prior.Count == 0)
                return new ChainVerificationReport(tenantId, fromSequence, toSequence, 0, false, first.SequenceNumber, first.Id, "prior event missing (chain break)");
            expectedPreviousHash = prior[0].ChainHash;
        }

        long expectedSeq = first.SequenceNumber;
        long checks = 0;
        foreach (var evt in events)
        {
            checks++;
            if (evt.SequenceNumber != expectedSeq)
            {
                return new ChainVerificationReport(tenantId, fromSequence, toSequence, checks, false, evt.SequenceNumber, evt.Id,
                    $"sequence gap: expected {expectedSeq}, got {evt.SequenceNumber}");
            }
            if (!string.Equals(evt.PreviousChainHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                return new ChainVerificationReport(tenantId, fromSequence, toSequence, checks, false, evt.SequenceNumber, evt.Id,
                    $"previousChainHash mismatch: stored='{evt.PreviousChainHash}', expected='{expectedPreviousHash}'");
            }

            string recomputed;
            if (evt.IsTombstoned)
            {
                // Tombstoned: trust the stored ContentHash (the payload has been redacted to a
                // deterministic stub). The chain link is independent of the payload bytes, so
                // verification still passes across pruned events.
                recomputed = HashChain.LinkHash(evt.ContentHash, evt.PreviousChainHash);
            }
            else
            {
                var canonical = CanonicalJson.Serialize(evt.PayloadJson);
                var recomputedContent = HashChain.ContentHash(canonical);
                if (!string.Equals(recomputedContent, evt.ContentHash, StringComparison.Ordinal))
                {
                    return new ChainVerificationReport(tenantId, fromSequence, toSequence, checks, false, evt.SequenceNumber, evt.Id,
                        "contentHash mismatch — payload was tampered with");
                }
                recomputed = HashChain.LinkHash(evt.ContentHash, evt.PreviousChainHash);
            }

            if (!string.Equals(recomputed, evt.ChainHash, StringComparison.Ordinal))
            {
                return new ChainVerificationReport(tenantId, fromSequence, toSequence, checks, false, evt.SequenceNumber, evt.Id,
                    "chainHash mismatch — chain broken at this sequence");
            }

            expectedPreviousHash = evt.ChainHash;
            expectedSeq = evt.SequenceNumber + 1;
        }

        return new ChainVerificationReport(tenantId, fromSequence, toSequence, checks, true, null, null, null);
    }

    public async Task<Checkpoint> WriteCheckpointAsync(string tenantId, CancellationToken ct)
    {
        var latest = await _events.GetLatestForTenantAsync(tenantId, ct);
        if (latest is null) throw new InvalidOperationException("No events to checkpoint.");

        var previous = await _checkpoints.LatestForTenantAsync(tenantId, ct);
        var fromSequence = (previous?.ToSequence ?? 0) + 1;
        var toSequence = latest.SequenceNumber;
        if (toSequence < fromSequence) throw new InvalidOperationException("No new events since last checkpoint.");

        var events = await _events.ListForVerificationAsync(tenantId, fromSequence, toSequence, ct);
        var leafHashes = events.Select(e => e.ChainHash).ToList();
        var root = MerkleTree.ComputeRoot(leafHashes);

        var checkpoint = Checkpoint.Create(
            Guid.NewGuid(),
            tenantId,
            fromSequence,
            toSequence,
            root,
            events[0].ChainHash,
            events[^1].ChainHash,
            events.Count,
            DateTimeOffset.UtcNow,
            null,
            null);

        var sig = _signing.SignBase64(System.Text.Encoding.UTF8.GetBytes(root));
        checkpoint.AttachSignature(sig, _signing.KeyId);

        await _checkpoints.AddAsync(checkpoint, ct);
        await _checkpoints.SaveChangesAsync(ct);
        return checkpoint;
    }

    /// <summary>Build a Merkle inclusion proof for the given event, relative to the most recent
    /// checkpoint that covers its sequence number.</summary>
    public async Task<InclusionProof?> BuildInclusionProofAsync(string tenantId, Guid eventId, CancellationToken ct)
    {
        var evt = await _events.GetByIdAsync(tenantId, eventId, ct);
        if (evt is null) return null;
        var checkpoint = await _checkpoints.FindCheckpointForSequenceAsync(tenantId, evt.SequenceNumber, ct);
        if (checkpoint is null) return null;

        var events = await _events.ListForVerificationAsync(tenantId, checkpoint.FromSequence, checkpoint.ToSequence, ct);
        var leaves = events.Select(e => e.ChainHash).ToList();
        var leafIndex = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Id == evt.Id) { leafIndex = i; break; }
        }
        if (leafIndex < 0) return null;

        var path = MerkleTree.BuildPath(leaves, leafIndex);
        return new InclusionProof(
            evt.Id, evt.SequenceNumber, evt.ChainHash,
            checkpoint.Id, checkpoint.MerkleRoot, path,
            checkpoint.SignatureBase64, checkpoint.SigningKeyId);
    }

    public bool VerifyInclusionProof(InclusionProof proof)
        => MerkleTree.VerifyPath(proof.LeafHash, proof.Path, proof.MerkleRoot);

    public bool VerifyCheckpointSignature(Checkpoint c)
    {
        if (c.SignatureBase64 is null) return false;
        return _signing.Verify(System.Text.Encoding.UTF8.GetBytes(c.MerkleRoot), c.SignatureBase64);
    }
}

public sealed record InclusionProof(
    Guid EventId,
    long SequenceNumber,
    string LeafHash,
    Guid CheckpointId,
    string MerkleRoot,
    IReadOnlyList<MerklePathStep> Path,
    string? SignatureBase64,
    string? SigningKeyId);
