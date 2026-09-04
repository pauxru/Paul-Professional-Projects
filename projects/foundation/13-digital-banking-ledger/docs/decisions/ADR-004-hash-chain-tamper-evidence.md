# ADR-004 — Hash-chain tamper evidence

- **Status**: Accepted
- **Date**: 2026
- **Context tags**: auditability, integrity, security

## Context

An append-only ledger prevents *legitimate* code paths from mutating history, but it does not, by
itself, prove that history was never altered out-of-band (e.g. someone editing the SQLite file or a
row directly in the database). A regulator or auditor needs to be able to **detect** any retroactive
change or reordering of entries.

## Options considered

1. **Trust the append-only guard alone** — rely on `SaveChanges` rejecting updates/deletes.
2. **Per-entry signature** — sign each entry independently.
3. **Hash chain** — each entry stores `hash = SHA-256(previousHash ‖ canonicalContent)`, linking
   entries so that any change breaks every subsequent link.

## Decision

Adopt a **hash chain** (option 3). When an entry is sealed it is assigned the next `SequenceNumber`
and a `Hash = SHA-256(previousHash ‖ "\n" ‖ canonicalContent)`, where the canonical content is a
deterministic serialization of the entry's fields and ordered postings, and the genesis predecessor
is 64 hex zeroes. Sealing happens under the global chain lock so the chain is a strict total order. A
verification endpoint (`GET /api/v1/admin/integrity/verify`) walks the whole chain, recomputes each
hash, and reports the first broken sequence if any — alongside a cached-vs-derived balance
reconciliation.

## Consequences

**Positive**
- **Tamper-evident**: altering any entry (or reordering entries) changes its hash and therefore breaks
  every later link; a full-chain walk detects it and pinpoints the first broken sequence. Proven by a
  unit test that mutates an entry and asserts detection.
- Cheap and dependency-free — just SHA-256 over a canonical string.
- Composes with append-only (ADR-001/append-only guard) and the reconciliation self-check into a
  single integrity report with an `IsHealthy` flag.

**Negative / costs**
- Sealing is serialized under the chain lock (already accepted in ADR-003).
- Verification is O(n) over all entries; for very large ledgers this would need checkpointing.

## Risks and mitigations

- **Risk**: an attacker who can write the database could recompute the entire chain from the point of
  edit forward, producing a self-consistent forged chain. **Mitigation (and honest limit)**: an
  in-database hash chain proves *internal* consistency, not external non-repudiation. Stronger proofs
  require periodically publishing the head hash to an independent/append-only external store
  (notarisation) — listed as future work. This is stated plainly in the security review.
- **Risk**: non-deterministic canonicalization would cause false positives. **Mitigation**: the
  canonical content uses invariant formatting and a fixed posting order; hashing is unit-tested.

## Alternatives not chosen

- *Append-only guard alone* — rejected: prevents in-app mutation but cannot detect out-of-band edits.
- *Independent per-entry signatures* — rejected: detects edits to a single entry but not reordering or
  deletion of entries; a chain covers both. (Signing the chain head is the natural future extension.)
