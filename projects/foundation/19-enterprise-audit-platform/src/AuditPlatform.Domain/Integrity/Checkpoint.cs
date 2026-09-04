using System.Text.Json.Serialization;

namespace AuditPlatform.Domain.Integrity;

/// <summary>
/// Periodic Merkle-tree root over a batch of events. Stored per tenant so a verifier can pick
/// any historical window and re-verify a single event using a Merkle inclusion proof.
/// </summary>
public sealed class Checkpoint
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public long FromSequence { get; private set; }
    public long ToSequence { get; private set; }
    public string MerkleRoot { get; private set; } = string.Empty;
    public string FirstChainHash { get; private set; } = string.Empty;
    public string LastChainHash { get; private set; } = string.Empty;
    public int LeafCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? SignatureBase64 { get; private set; }
    public string? SigningKeyId { get; private set; }

    private Checkpoint() { }

    // Public constructor exclusively for System.Text.Json round-tripping (used by tests and the
    // /verify/inclusion endpoint payload). Domain code always goes through <see cref="Create"/>.
    [JsonConstructor]
    public Checkpoint(
        Guid id,
        string tenantId,
        long fromSequence,
        long toSequence,
        string merkleRoot,
        string firstChainHash,
        string lastChainHash,
        int leafCount,
        DateTimeOffset createdAt,
        string? signatureBase64,
        string? signingKeyId)
    {
        Id = id;
        TenantId = tenantId;
        FromSequence = fromSequence;
        ToSequence = toSequence;
        MerkleRoot = merkleRoot;
        FirstChainHash = firstChainHash;
        LastChainHash = lastChainHash;
        LeafCount = leafCount;
        CreatedAt = createdAt;
        SignatureBase64 = signatureBase64;
        SigningKeyId = signingKeyId;
    }

    public static Checkpoint Create(
        Guid id,
        string tenantId,
        long fromSequence,
        long toSequence,
        string merkleRoot,
        string firstChainHash,
        string lastChainHash,
        int leafCount,
        DateTimeOffset createdAt,
        string? signatureBase64,
        string? signingKeyId)
        => new(id, tenantId, fromSequence, toSequence, merkleRoot, firstChainHash, lastChainHash, leafCount, createdAt, signatureBase64, signingKeyId);

    public void AttachSignature(string signatureBase64, string signingKeyId)
    {
        SignatureBase64 = signatureBase64;
        SigningKeyId = signingKeyId;
    }
}
