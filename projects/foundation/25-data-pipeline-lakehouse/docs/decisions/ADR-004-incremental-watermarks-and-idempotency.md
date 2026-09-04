# ADR-004: Incremental processing via watermarks with deterministic, idempotent rebuilds

- Status: Accepted
- Date: 2026-09
- Deciders: Solo engineer (self-directed case study)

## Context

The pipeline must process new data incrementally (not reprocess the whole history every run), resume
cleanly after a failure without duplicating data, and support backfilling a date range. At the same
time, re-running a window must **not** change results — idempotency is the property that makes a data
platform safe to operate (retries, backfills, and partial re-runs all depend on it).

There is a tension: ingestion should be **append-only and incremental** (cheap, exactly-once-effective),
while downstream layers must be **idempotent** even though they carry stateful concerns (SCD2 intervals,
surrogate keys, aggregates).

## Options considered

- **A. Full reprocessing every run.** Trivially idempotent but does not scale and defeats the point of
  incrementality; also can't express "resume" or "backfill just this window".
- **B. Incremental everywhere, with mutable in-place updates.** Cheapest, but in-place mutation makes
  resume-after-failure and idempotency hard (a half-applied run corrupts state) and reintroduces the
  duplication problems we are trying to avoid.
- **C. Incremental ingestion + deterministic full rebuild of derived layers.** Bronze ingests only new
  source records (tracked by a per-entity **watermark**) and is append-only/immutable with an
  idempotency token per batch. Silver/gold are **deterministic functions of immutable bronze**: each
  build recomputes its output by overwrite/merge, so re-running yields byte-for-byte identical results.

## Decision

Adopt **C**. `BronzeIngestor.Ingest` advances a watermark (`bronze:{entity}`) and records an idempotency
token in the snapshot summary, so a failed run that is replayed appends nothing new (checkpoint resume
without duplication). `IngestWindow(from,to)` supports range backfill. `SilverBuilder` is a
deterministic full rebuild from the immutable bronze log (dedup by business key + sequence, SCD2,
quarantine via delete-by-source-then-append), and `GoldBuilder` overwrites facts/dims/aggregates from
silver. Each source's quarantine slice is **replaced**, never accumulated, on re-run.

`IClock` is injected everywhere time matters (never `DateTime.UtcNow` in engine code), so snapshot
timestamps and time-travel are deterministic under a `FakeClock` in tests.

## Consequences

- Positive: idempotency is **structural**, not best-effort, and is asserted
  (`PipelineTests.Rerunning_the_same_window_is_idempotent` — identical fact count and revenue sum;
  `BronzeIngestionTests` — idempotent re-ingest and checkpoint resume with zero duplicates). Backfill
  and partial re-run (downstream closure) fall out naturally
  (`PipelineTests.Backfill_of_a_date_range_materialises_gold`, `DagTests`).
- Positive: because derived layers are pure functions of immutable bronze, a bug fix is applied simply
  by re-running — no migration of mutated state.
- Negative: a full rebuild of silver/gold is more work per run than a true incremental merge would be.
  Accepted for correctness and simplicity at this scale; the throughput test shows it handles
  >= 100,000 source rows within budget. A future optimisation is documented (partition-scoped rebuilds).

## Risks

- Full rebuild cost grows with total history. Mitigation: watermark-scoped ingestion keeps bronze
  growth incremental; rebuild can later be narrowed to affected partitions behind the same idempotency
  contract.
- Watermark/idempotency-token correctness under concurrency. Mitigation: the DAG runner enforces
  single-writer-per-window (`OverlappingRunException`); overlapping runs of the same window are rejected.

## Alternatives not chosen

Full reprocessing (A) — not incremental, no resume/backfill semantics. In-place mutation (B) — makes
resume and idempotency fragile and reintroduces duplication risk.
