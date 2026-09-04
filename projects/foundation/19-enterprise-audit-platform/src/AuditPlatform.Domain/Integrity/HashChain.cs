using System.Security.Cryptography;
using System.Text;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Serialization;

namespace AuditPlatform.Domain.Integrity;

/// <summary>
/// SHA-256 hash-chain construction.
///
///     content         = canonicalJson(eventPayload)             (deterministic — see CanonicalJson)
///     contentHash     = SHA256(content)                         (hex string)
///     chainHash[i]    = SHA256(contentHash[i] || "|" || chainHash[i-1])
///     chainHash[0]    = SHA256(contentHash[0] || "|" || GenesisHash(tenant))
///
/// Rationale for chaining by <b>contentHash</b> (not the raw canonical bytes): retention pruning
/// converts an event's PayloadJson to a tombstone stub while preserving <see cref="AuditEvent.ContentHash"/>
/// verbatim. Because the chain link only depends on the content hash, verification continues to
/// pass across pruned events — the pruning-vs-immutability reconciliation documented in ADR-004.
///
/// Hashes are stored as lowercase hex so they are safe to embed in JSON, cheap to index in
/// SQLite, and comparable across environments without base64 padding surprises.
/// </summary>
public static class HashChain
{
    /// <summary>Per-tenant genesis prefix. Distinct across tenants so we can never accidentally
    /// splice two tenants' chains together.</summary>
    public const string GenesisPrefix = "audit-genesis:";

    private const char LinkSeparator = '|';

    public static string GenesisHash(string tenantId)
    {
        var seed = Encoding.UTF8.GetBytes(GenesisPrefix + tenantId);
        return Hex(SHA256.HashData(seed));
    }

    public static string ContentHash(byte[] canonicalPayload) => Hex(SHA256.HashData(canonicalPayload));

    /// <summary>Compute the chain link hash: SHA256(contentHash || "|" || previousChainHash).
    /// Both operands are hex strings; this makes the computation entirely a function of stored
    /// per-event data, so tombstoning preserves chain verifiability.</summary>
    public static string LinkHash(string contentHash, string previousChainHash)
    {
        var bytes = Encoding.UTF8.GetBytes(contentHash + LinkSeparator + previousChainHash);
        return Hex(SHA256.HashData(bytes));
    }

    /// <summary>Backwards-compatible link over the canonical payload bytes: computes the content
    /// hash first, then the chain link. Used at ingest time so callers only need to pass the
    /// canonical bytes once.</summary>
    public static string LinkHashFromCanonical(byte[] canonicalPayload, string previousChainHash)
        => LinkHash(ContentHash(canonicalPayload), previousChainHash);

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0) throw new ArgumentException("Hex string must have an even length.", nameof(hex));
        var buf = new byte[hex.Length / 2];
        for (var i = 0; i < buf.Length; i++)
        {
            buf[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return buf;
    }

    /// <summary>Recompute the chain hash for a single event given its content hash and the
    /// previous chain hash. This is the primitive verification uses to walk the chain.</summary>
    public static string Recompute(string contentHash, string previousChainHash)
        => LinkHash(contentHash, previousChainHash);

    /// <summary>Verify that an event's stored ChainHash matches the recomputed link hash.</summary>
    public static bool Verify(AuditEvent evt)
        => string.Equals(LinkHash(evt.ContentHash, evt.PreviousChainHash), evt.ChainHash, StringComparison.Ordinal);

    /// <summary>Compute canonical bytes for the *payload* portion of an event.</summary>
    public static byte[] Canonical(string payloadJson) => CanonicalJson.Serialize(payloadJson);
}
