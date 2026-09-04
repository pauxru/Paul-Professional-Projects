# Integrity model

This document describes the cryptographic construction of the audit chain precisely enough
that a verifier written in any language can reproduce it.

## Primitives

- **Hash function**: SHA-256, single application, output stored as lowercase hex.
- **Signature**: RSA 2048-bit, PKCS#1 v1.5 (as implemented by .NET
  `RSA.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)`).
- **Canonical serialisation**: see ADR-002 for the seven rules. Reference implementation:
  `AuditPlatform.Domain.Serialization.CanonicalJson`.

## Per-tenant genesis

```
genesis(tenantId) = SHA256( "audit-genesis:" || tenantId )
```

The prefix guarantees that two distinct tenants can never share a genesis hash; therefore two
chains from different tenants cannot be spliced together.

## Chain construction

For each event *i* in tenant *T*:

```
canonical[i]  = canonicalJson(payload[i])
contentHash[i] = SHA256(canonical[i])
prev[i]        = genesis(T)                if i == 1
                 chainHash[i-1]            otherwise
chainHash[i]  = SHA256( contentHash[i] || "|" || prev[i] )
```

- The chain link is a function of the **contentHash** rather than the raw canonical bytes.
  This decoupling is critical for retention (see ADR-004): tombstoning wipes the payload but
  preserves the contentHash, so recomputation still matches the stored chainHash.
- All hashes are hex-encoded (lowercase) strings before concatenation with `"|"`. Cross-language
  verifiers should be careful to concatenate hex text, not raw bytes.

## Verification

Given a range `[fromSequence, toSequence]`:

1. If the range starts at sequence 1, set `expectedPrev = genesis(tenantId)`. Otherwise fetch
   the immediately-preceding event and set `expectedPrev = chainHash[from-1]`.
2. For each event in order:
   - If `evt.SequenceNumber != expected`, report **sequence gap** at `evt.SequenceNumber`.
   - If `evt.PreviousChainHash != expectedPrev`, report **previousChainHash mismatch**.
   - If `evt.IsTombstoned`:
     - Compute `recomputed = SHA256( evt.ContentHash || "|" || evt.PreviousChainHash )`.
     - If `recomputed != evt.ChainHash`, report **chainHash mismatch**.
   - Else (live event):
     - Compute `canonical = canonicalJson(evt.PayloadJson)`.
     - Compute `recomputedContent = SHA256(canonical)`. If it differs from `evt.ContentHash`,
       report **contentHash mismatch — payload was tampered with**.
     - Compute `recomputed = SHA256( evt.ContentHash || "|" || evt.PreviousChainHash )`. If
       it differs from `evt.ChainHash`, report **chainHash mismatch**.
   - Advance: `expectedPrev = evt.ChainHash`, `expected = evt.SequenceNumber + 1`.

## Merkle checkpoints

A checkpoint bounds a range `[fromSequence, toSequence]`. Its Merkle tree is built over the
`chainHash` of each event in the range, in ascending sequence order.

### Merkle rules (documented so a verifier can reproduce)

- Every leaf is a 32-byte SHA-256 hash exchanged as lowercase hex; convert to bytes for hashing.
- `parent(left, right) = SHA256( leftBytes || rightBytes )`.
- Odd tails: if a level has an odd number of nodes, the last node is duplicated to pair with
  itself. This is the Bitcoin/RFC-6962-lite convention.
- The root is the single node remaining after log₂(n) reductions.

### Inclusion proof

For a leaf at index *i* in a checkpoint of *n* leaves, the proof is a list of `(siblingHash,
isRight)` pairs recorded during the climb:

```
if idx is even, sibling = leaves[idx+1] if idx+1 < len else leaves[idx]  (right sibling)
else            sibling = leaves[idx-1]                                    (left sibling)
```

A verifier rebuilds the root by folding the leaf hash with each step:

```
current = leafHashBytes
for step in path:
    current = SHA256( current || siblingBytes ) if step.isRight
              else SHA256( siblingBytes || current )
return hex(current) == expectedRoot
```

## Signed checkpoints

The Merkle root is signed with an RSA key held by the platform:

```
signature = RSA.SignData( utf8(merkleRoot), SHA256, PKCS1v15 )
```

Verifying: `RSA.VerifyData( utf8(merkleRoot), signature, SHA256, PKCS1v15 )`. The `SigningKeyId`
stored alongside the signature identifies which public key to use when the key has been rotated.

## Evidence pack

```
{
  "tenantId": "...",
  "from": "...",
  "to":   "...",
  "events": [ ...canonical event dtos... ],
  "checkpoints": [ ...checkpoints with signatures... ],
  "manifest": {
    "eventCount": <n>,
    "checkpointCount": <k>,
    "bundleHash": "SHA256 of the canonicalised events+checkpoints"
  },
  "signature": "<RSA over bundleHash>",
  "signingKeyId": "..."
}
```

Verification checks: signature over `bundleHash`, recompute `bundleHash` from serialised
contents, and (optionally) walk the chain over the included events.

## Notes on tombstones

The tombstone payload is deterministic:

```
{"tombstone":true,"contentHash":"<original hex>"}
```

but this payload is never re-hashed during verification. Verification uses only `ContentHash`
and `PreviousChainHash`, both of which are preserved on a tombstone. This is the reconciliation
between retention and immutability.
