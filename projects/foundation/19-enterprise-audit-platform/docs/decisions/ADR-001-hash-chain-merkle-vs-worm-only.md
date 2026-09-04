# ADR-001: Hash chain plus Merkle checkpoints, not WORM-only

*Status*: Accepted
*Date*: 2026-01-15
*Owners*: platform

## Context

We need to prove that no audit event has been added, removed, altered, or reordered without
detection. Two established approaches exist:

- **WORM (Write-Once-Read-Many) storage** as the sole integrity mechanism — rely on a storage
  medium that structurally refuses `UPDATE` and `DELETE`. Examples: append-only S3 buckets with
  Object Lock, appliance-based WORM disks.
- **Cryptographic hash-chain + periodic Merkle checkpoints** — every event is hashed together
  with the previous event's hash to produce a chain hash; periodically a Merkle root over a
  batch is signed and published.

## Decision

We combine **both**, but implement the cryptographic chain + Merkle checkpoints in this
codebase and describe WORM as production hardening.

## Rationale

- **WORM alone is fragile against insiders with root**: an operator who can rotate the WORM
  storage backend can silently substitute a whole tenant chain. Cryptographic linkage means a
  substitution leaves detectable evidence.
- **Merkle checkpoints let auditors verify a single event cheaply**: an inclusion proof is
  O(log n) hashes and requires no trust in the operator. This is a huge win compared to
  streaming the whole log.
- **Independence**: WORM protects against *physical* mutation; hash chaining protects against
  *any* mutation regardless of storage. Belt and braces.

## Consequences

- We must implement a deterministic canonical serialisation. See ADR-002.
- Retention pruning has to reconcile with the chain: see ADR-004.
- Chain writes must be strictly serialised per tenant. See ADR-005.
- The signing key must be stored somewhere non-repudiable in production. This is out of scope
  for the local build; a KMS/HSM is the obvious answer.

## Alternatives considered

- **WORM-only** — rejected for insider-threat reasons.
- **Ledger DB (QLDB, Amazon-style)** — brings the same cryptographic properties, but the
  point of this exercise is to build the primitives, not to consume them.
- **Blockchain** — orders of magnitude more complexity for what is a per-tenant, private log.
