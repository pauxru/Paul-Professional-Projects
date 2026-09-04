# ADR-001: Implement a small Delta-like table format instead of adopting Delta/Iceberg (and JSONL data files instead of Parquet)

- Status: Accepted
- Date: 2026-09
- Deciders: Solo engineer (self-directed case study)

## Context

The project must demonstrate that lakehouse table-format concepts — atomic commits, snapshot
isolation, time travel, schema evolution, MERGE upsert and delete-by-predicate — are genuinely
understood, and it must build and test with **zero external infrastructure** on a Windows host that
has only the .NET SDK (no Docker, Spark, Databricks, Python, or a JVM-based Delta/Iceberg runtime).

Two separable decisions live here:

1. **The transaction protocol** — do we take a dependency on Delta Lake / Apache Iceberg, or build a
   minimal transaction-log-backed format ourselves?
2. **The physical data-file encoding** — Parquet vs something simpler.

## Options considered

### Transaction protocol
- **A. Adopt Delta Lake / Iceberg via a runtime.** Industry-standard, battle-tested ACID semantics.
  But the mature implementations are JVM/Spark-centric; the .NET ports are partial and would still
  need a compute engine. Fails the zero-infrastructure prime directive and hides the very concepts
  the project exists to demonstrate.
- **B. Build a minimal transaction log ourselves.** A manifest/commit-log per table: each commit is an
  atomic snapshot referencing immutable data files, with a registered schema per version. Readers pin
  a snapshot (isolation); old snapshots enable time travel; MERGE/DELETE are copy-on-write producing
  new snapshots. Smaller scope, fully in-process, and every concept is visible and testable.

### Data-file encoding
- **C. Parquet via `Parquet.Net` 6.1.0.** Columnar, the real lakehouse format. It resolves on the
  feed, but its untyped read path throws `Cannot create boxed ByRef-like values` on .NET 10 — a
  runtime incompatibility outside our control.
- **D. DuckDB.NET.** Resolves, but pulls a native engine and reframes the project around DuckDB rather
  than a from-scratch format.
- **E. JSONL behind an `IDataFileFormat` abstraction.** Trivially correct, human-readable (great for
  debugging and for reviewers), no native dependency. Not columnar, so larger on disk and slower to
  scan — acceptable for a demonstrator, and swappable later because it sits behind an interface.

## Decision

Adopt **B** (own transaction log) with **E** (JSONL data files behind `IDataFileFormat`).

The format (`FileSystemLakehouse` / `LakeTable`) provides: append-only immutable data files
partitioned per commit; a snapshot chain (`Snapshot` with parent id, timestamp, operation, schema
version, added/removed files); atomic commit (write files, then atomically publish the new snapshot);
snapshot-isolated `Scan(snapshotId?)` and time-travel `ScanAsOf(timestamp)`; additive `EvolveSchema`;
copy-on-write `Overwrite`, `Merge(keys)` and `Delete(predicate)`.

## Consequences

- Positive: builds and tests with only the .NET SDK; the concepts are demonstrable and unit-tested
  (atomic commit, snapshot isolation, time travel, MERGE, delete, schema-evolution read of old+new
  files). The `IDataFileFormat` seam means Parquet can be reinstated when the read path is fixed,
  without touching the transaction log.
- Negative / honest scope — what this is **not**: no concurrent multi-writer protocol (single-process,
  last-writer-wins per table; a real lock/optimistic-concurrency layer is out of scope); no data-file
  compaction, no Z-ordering/clustering, no statistics-based file skipping; JSONL is row-oriented, so
  predicate pushdown and column pruning are not exploited; no vacuum/retention policy beyond keeping
  the snapshot chain. These are exactly the areas Delta/Iceberg invest in, and they are named so a
  reviewer sees the boundary clearly.

## Risks

- A reader could mistake the demonstrator for a production Delta replacement. Mitigation: this ADR,
  the README "Known Limitations", and explicit in-code XML docs state the non-goals.
- JSONL disk footprint on large volumes. Mitigation: the throughput test runs >= 100,000 rows within
  budget; the abstraction allows a columnar format later.

## Alternatives not chosen

Adopting Delta/Iceberg (A) — violates zero-infra and hides the learning objective. Parquet (C) —
blocked by the .NET 10 read bug. DuckDB (D) — would make the project about DuckDB, not about building
a table format.
