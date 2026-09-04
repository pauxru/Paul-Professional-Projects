# ADR-001 — Text conflict model: RGA CRDT vs Operational Transformation

- **Status:** Accepted
- **Date:** 2026-09-03
- **Decision owner:** (self-directed engineering case study)
- **Supersedes / superseded by:** —

## Context

The headline requirement is *correct concurrent editing of plain-text documents with an honestly
documented conflict model*. Two people editing the same runbook line at the same time must converge
to the same text on every replica, causality must be respected, and the server must be authoritative
for ordering and persistence. The two mainstream, genuinely-correct approaches are:

1. **Operational Transformation (OT)** — clients send operations expressed against a document
   version; the server (and clients) **transform** each incoming operation against concurrent ones so
   it applies correctly to the current state. This is what Google Docs and classic Jupiter/ot.js use.
2. **CRDT** — operations are designed to be **commutative** so they can be merged in any order with no
   transformation. For sequences, the well-known families are RGA (a causal tree), Logoot/LSEQ
   (dense position identifiers), and Yjs/Automerge-style implementations.

This is *the* central decision of the project, so it gets its own ADR with an explicit limitations
section (see also `docs/conflict-resolution.md`).

## Options considered

### Option A — Operational Transformation (server-side transform, e.g. Jupiter)
- **Pros:** Compact operations (`retain/insert/delete` with integer offsets); no per-character
  identifier overhead; battle-tested in the largest collaborative editor in the world; naturally
  models a single server-authoritative stream.
- **Cons:** Correctness is notoriously subtle. The transformation function must satisfy transform
  properties (TP1, and TP2 if you allow peer-to-peer); getting `transform(ins, del)` and
  `transform(del, del)` right for every adjacency/overlap case is where most real-world OT bugs live.
  A property-based convergence test is essential *and* hard because the space of transforms is large.

### Option B — RGA CRDT (causal tree) — **chosen**
- **Pros:** Convergence is a *structural* property — text is a pure function of the operation set — so
  the correctness argument is far simpler and directly testable by "apply the same ops in any order,
  assert identical output". No transform function to get wrong. Optimistic local editing reconciles by
  **merge, not rebase**, which removes an entire class of client bugs. Deletion as tombstone is
  trivially idempotent. Maps cleanly onto a server-authoritative log while still being mergeable.
- **Cons:** Every character carries an `(lamport, replica)` id → memory and payload overhead. Deleted
  characters live on as tombstones until compaction. Concurrent typed runs can interleave at the
  character level. Requires snapshotting to keep load fast.

### Option C — Logoot / LSEQ (position-identifier CRDT)
- **Pros:** No tombstones for deletes; positions are dense fractional identifiers.
- **Cons:** Identifier allocation strategy strongly affects identifier growth (LSEQ exists precisely to
  fight Logoot's identifier bloat); interleaving anomalies still exist; more fiddly to implement
  correctly than RGA for a first-class, from-scratch implementation.

### Option D — Adopt a library (Yjs via a .NET port, Automerge)
- **Pros:** Mature, fast, well-tested.
- **Cons:** The entire point of this case study is to **demonstrate** that I can implement and defend a
  conflict model, not to `import` one. A vendored library would hide exactly the engineering the
  portfolio is meant to show. Rejected on those grounds, not technical ones.

## Decision

Implement a **from-scratch RGA CRDT (causal tree)** for plain text, with the **server authoritative**
for sequence assignment and persistence, and a **causal buffer** for out-of-order operations. Prove
convergence with a randomised property test (fixed seed). Use a **separate** strategy — field-level
LWW with version vectors — for structured documents (see ADR-002-style reasoning in
`docs/conflict-resolution.md`), because forcing one algorithm onto both data shapes would be wrong.

## Consequences

**Positive**
- The convergence proof is simple, honest, and mechanised (property test + exhaustive pair tests).
- Optimistic client editing needs no transform function; remote ops merge into the local replica.
- The design composes with an append-only server log, snapshots, time-travel and restore.

**Negative / costs**
- Per-character id overhead and tombstone accumulation → mitigated (not eliminated) by periodic
  snapshots; full distributed tombstone GC is deliberately **not** implemented.
- Character-level interleaving of concurrent runs is possible and is documented as a known limitation.

## Risks and mitigations
- **Risk:** State grows unbounded with edit volume. **Mitigation:** periodic snapshots compact load;
  documented limit; GC noted as future work.
- **Risk:** Subtle CRDT bug ships silently. **Mitigation:** randomised convergence property test with a
  fixed seed plus exhaustive transform-pair unit tests; the JS client port is independently fuzzed.
- **Risk:** Reviewers expect "Google Docs quality" merges. **Mitigation:** `conflict-resolution.md`
  states plainly that CRDTs guarantee convergence, not intention-level merge.

## Honest limitations (required)
RGA guarantees **strong eventual consistency**, not semantic merge. It can interleave concurrent
typed sentences at the character level. Tombstones accumulate. Only plain text is modelled (no rich
text). Ordering is **logical** (Lamport), not wall-clock. See `docs/conflict-resolution.md` §1.6 for
the full, blunt list. These are the correct trade-offs for the scope, stated openly.

## Alternatives not pursued
OT (Option A) — rejected for correctness-surface and testing difficulty relative to the guarantees
gained. Logoot/LSEQ (C) — rejected as harder to implement correctly than RGA for equivalent behaviour.
Third-party CRDT library (D) — rejected because it would defeat the purpose of the case study.
