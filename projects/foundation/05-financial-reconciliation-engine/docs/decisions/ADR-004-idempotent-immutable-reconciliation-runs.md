# ADR-004: Idempotent Immutable Reconciliation Runs

## Status

Accepted, dated 2026-09.

## Context

`ReconciliationOrchestrator.RunAsync` loads the working set through `GetWorkingSetAsync`, which returns records whose `ReconStatus != Matched` within an optional value-date window. It orders internal and external records by `RowHash`, runs the pure `ReconciliationCalculator`, and persists a `ReconciliationRun` snapshot with `RuleSetId`, `RuleSetVersionTag`, `InputChecksum`, counts, totals JSON, exception breakdown JSON, balance assertion fields, timestamps, and duration.

Re-runs must be safe. Existing open exceptions are keyed by stable `ExceptionKey`; the orchestrator preserves already triaged exceptions when the key still exists, inserts only new keys, removes in-scope exceptions that are no longer desired, and replaces matches for records in the working set. `InputChecksum` is computed from working-set row hashes using `RowHasher.Checksum`.

Known carry-forward behaviour is intentional: matched records, including pairs that were also flagged as `FeeVariance` or `StatusMismatch`, leave the working set. A second run over identical inputs re-evaluates only still-open records. Its in-scope `ExceptionCount` is therefore less than or equal to the first run's, while the total open exception set remains stable with no duplication. This has been verified with seed 7 / 1000 rows: first run produced 250 exceptions, and total open stayed 250 across re-runs.

## Decision

Persist every reconciliation as an immutable `ReconciliationRun` snapshot and make re-runs idempotent for the current working set. Idempotency is achieved by:

- deriving run `InputChecksum` from working-set row hashes;
- deleting and recreating matches for in-scope working records;
- upserting exceptions by stable `ExceptionKey` while preserving existing triage state;
- carrying unmatched records forward with `ReconStatus.Exception`;
- excluding matched records from future working sets through `ReconStatus.Matched`.

If the balance assertion fails, the run is marked `Failed` and `BalanceAssertionException` is thrown. The engine fails loudly rather than silently persisting an apparently successful inconsistent run.

## Options Considered

1. Immutable run snapshots with idempotent re-run semantics.
   - Pros: audit history is durable; re-runs do not duplicate exceptions; triage work is preserved; working-set input can be fingerprinted.
   - Cons: readers must understand in-scope vs total open exception counts; storing full snapshots creates more rows over time.
2. Mutable single current reconciliation state.
   - Pros: simpler queries for the latest state; less historical data.
   - Cons: weak audit trail; harder to explain prior reports; accidental overwrites can destroy evidence.
3. Append-only everything with no idempotent upsert.
   - Pros: every calculator output is preserved exactly.
   - Cons: duplicate exceptions on re-run; triage queues become noisy; manual resolution cannot easily carry forward.
4. Always reprocess all records, including matched records.
   - Pros: run-to-run counts are easier to compare; policy changes can revisit all matches.
   - Cons: more work; stable matches churn; flagged-but-matched records would repeatedly reappear in the working set.

## Consequences

Positive consequences:

- Re-running identical inputs does not multiply open exceptions.
- Existing assignments, comments, approvals, and resolution workflow state can be preserved by exception key.
- Immutable `runs` rows provide historical counts, rule version, checksum, and balance assertion evidence.
- Matched records leaving the working set keeps later runs focused on unresolved work.

Negative consequences:

- `run.ExceptionCount` is the in-scope open count for that run, not necessarily the global open-exception total.
- Matched records with flagged conditions such as fee variance are excluded from later matching unless explicitly reopened or reset by future workflow.
- Storage grows with every run snapshot.

## Risks

- Risk: operators misread a lower second-run `ExceptionCount` as lost exceptions. Mitigation: document the carry-forward rule and expose total open exception reports separately.
- Risk: unstable `ExceptionKey` generation would duplicate exceptions. Mitigation: keep keys content-derived and covered by tests.
- Risk: balance assertion failures disrupt automated runs. Mitigation: this is desired fail-loud behaviour; failed runs retain `BalanceAssertionDetail` for investigation.

## Alternatives

A reviewer might expect a ledger-style append-only event stream for every match and exception change. That was not chosen for the current modular monolith because immutable run snapshots plus append-only exception audit entries provide the required auditability with simpler EF Core and SQLite persistence. A full event-sourced model can be revisited if reconstruction from events becomes a hard requirement.
