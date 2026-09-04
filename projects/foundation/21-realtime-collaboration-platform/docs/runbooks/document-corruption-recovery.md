# Runbook — Document corruption recovery

**Symptom:** a document renders incorrectly, fails to load, diverges between clients, or a snapshot is
suspected bad. Because content is **derived** from an append-only log (ADR-003), recovery is almost
always possible without data loss.

## 0. Mental model (why recovery works)

A document's content is **not** a stored blob you can lose. It is:

```
content(at sequence k) = replay( operation_log where ServerSequence <= k
                                 starting from the newest snapshot with AtSequence <= k )
```

The **operation log is the source of truth**. Snapshots are just a cache to make load fast. So a bad
snapshot is recoverable, and even a "corrupt" render is usually reproducible and explainable from the
log.

## 1. Triage — is it really corruption?

1. **Reproduce read-only.** Use the time-travel endpoint to read the document at successive
   sequences and find where it goes wrong:
   `GET /api/v1/documents/{id}/at/{sequence}` for sequences around the suspected point.
2. **Check convergence, not correctness.** CRDT convergence guarantees all replicas agree, **not**
   that a human-intended merge happened. Interleaved concurrent typing (documented in
   `conflict-resolution.md`) is **expected behaviour**, not corruption. Confirm the clients actually
   **disagree** (true divergence) before treating it as corruption.
3. **Inspect the log.** Query `operation_log` for the document ordered by `ServerSequence`; confirm it
   is **gap-free and monotonic** (the unique `(DocumentId, ServerSequence)` index should guarantee
   this). A gap or duplicate is the real red flag.

## 2. Recover from a bad snapshot (most common)

If load is slow/wrong but the log is intact:

1. Identify the latest snapshot for the document (`snapshots` where `DocumentId = …` by `AtSequence`).
2. **Delete the suspect snapshot row.** Loading falls back to the previous snapshot (or to full replay
   from sequence 0 if none remains) — both reproduce the document because the log is authoritative.
3. Trigger a reload (rejoin the document). The engine will replay from the previous checkpoint and,
   after `SnapshotEveryNOperations`, write a fresh, correct snapshot.
4. Verify with the snapshot+replay integrity check (the behaviour asserted by `SnapshotReplayTests`):
   replay must reproduce the rendered content exactly.

## 3. Recover when the whole document must be rebuilt

If snapshots are untrustworthy:

1. Rebuild purely from the log: replay `operation_log` from `ServerSequence = 0` to head.
2. Compare the rebuilt render to what clients report. If they match the log-derived content, the
   clients are correct and any cached snapshot was the problem (go to §2).
3. If a specific operation is provably malformed (e.g. references a node that never existed), that
   entry is the corruption. **Do not delete or edit log history** — instead:
   - stop new writes to the document (revoke `Edit` temporarily by role, or take the instance out of
     rotation),
   - create a **named version** at the last-known-good sequence
     (`POST /api/v1/documents/{id}/versions`),
   - **restore** to that sequence (`POST /api/v1/documents/{id}/restore`), which appends **forward**
     compensating operations (never a rewrite), preserving the full audit trail.

## 4. Roll back user-visible state without rewriting history

Never mutate `operation_log`. The supported, auditable rollback is **restore-as-forward**:

```
POST /api/v1/documents/{id}/restore   { "toSequence": <last-good-seq> }
```

This diffs current → target and applies the difference as new operations. History stays intact and
"who restored what, when" is captured in `audit_records`.

## 5. Data integrity checks to run afterward

- `operation_log` unique `(DocumentId, ServerSequence)` holds (no gaps/dupes).
- `Document.CurrentSequence` equals the max `ServerSequence` in the log for that document.
- Latest snapshot's `MaterializedContent` equals a fresh replay to its `AtSequence`.
- Any text-anchored comments still resolve (rebased) or are flagged `IsOrphaned` — no dangling ranges.

## 6. Prevention

- Keep snapshots frequent enough to bound replay cost but rely on the **log** as truth.
- Never expose a path that updates/deletes `operation_log` or `audit_records` from application code.
- Keep the randomised convergence property test (fixed seed) in CI so a CRDT regression is caught
  before it can corrupt real documents.
- Back up the SQLite file (or the Postgres database) regularly; the log-based model makes
  point-in-time reconstruction straightforward.
