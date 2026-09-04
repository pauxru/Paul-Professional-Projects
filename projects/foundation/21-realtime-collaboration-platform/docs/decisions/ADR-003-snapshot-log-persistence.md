# ADR-003 — Persistence: append-only operation log + periodic snapshots

- **Status:** Accepted
- **Date:** 2026-09-03

## Context

A document's content is the result of many small operations over time. We need: fast load, full
history, time-travel to any version, named versions, diffs, restore, and a replay path that provably
reproduces the exact document. All of this on **SQLite by default, no external infrastructure.**

## Options considered

### Option A — Store only the current materialized content
- **Pros:** Trivial; one row per document.
- **Cons:** No history, no time-travel, no audit of *how* content changed, no CRDT state to merge late
  operations against. Fundamentally incompatible with the requirements. Rejected.

### Option B — Event sourcing: append-only operation log, no snapshots
- **Pros:** Perfect history; the log is the single source of truth; replay reproduces any version.
- **Cons:** Loading a long-lived document means replaying its entire log every time → O(total edits)
  load cost. Unacceptable for hot documents.

### Option C — Operation log **+ periodic snapshots** — **chosen**
- **Pros:** Keeps the append-only log as the source of truth (full history, time-travel, audit,
  replay) **and** bounds load cost: load = *latest snapshot* + replay of the *tail* since that
  snapshot. Snapshots also carry the rendered content for cheap previews and integrity checks.
- **Cons:** Two write paths (log always; snapshot every *N*); snapshots duplicate state; a snapshot
  cadence to tune.

### Option D — CRDT state blob only, overwritten each edit
- **Pros:** Simple load (one blob).
- **Cons:** Loses the operation history and the authoritative sequence axis; no time-travel; no
  per-operation audit. Rejected.

## Decision

Model each accepted change set as an **`OperationLogEntry`** with a **monotonic, gap-free
`ServerSequence`** unique per document. Every *N* operations (default **200**, configurable via
`Collaboration:SnapshotEveryNOperations`) write a **`DocumentSnapshot`** holding the serialized CRDT
state and the rendered content at that sequence. Load = latest snapshot + tail replay. `Document.
CurrentSequence` caches the authoritative version. Restore is a **forward** operation (diff → new
ops), never a history rewrite.

## Consequences

**Positive**
- Fast load regardless of document age (bounded by snapshot cadence).
- Full history, time-travel read at any sequence, named versions, and diff all fall out naturally.
- A test asserts **snapshot + log replay reproduces the document byte-for-byte** — the integrity
  contract is mechanised.
- The log doubles as forensic/audit evidence of exactly how content evolved.

**Negative**
- Storage grows with total edits (log + snapshots). Old log entries could be pruned once a covering
  snapshot exists; we **retain** them to preserve full time-travel and audit — a deliberate
  space-for-truth trade.
- Snapshot cadence is a tuning knob: too rare → slow load; too frequent → storage churn.

## Risks and mitigations
- **Risk:** A corrupt or missing snapshot blocks load. **Mitigation:** load can always fall back to
  full log replay from sequence 0; see `docs/runbooks/document-corruption-recovery.md`.
- **Risk:** Sequence gaps would silently corrupt version math. **Mitigation:** a unique index on
  `(DocumentId, ServerSequence)` enforces gap-free monotonicity; assignment happens inside the
  persistence step under the document's serialization.

## Alternatives not pursued
Current-content-only (A) and CRDT-blob-only (D) — rejected for losing history. Pure event sourcing
without snapshots (B) — rejected for unbounded load cost.
