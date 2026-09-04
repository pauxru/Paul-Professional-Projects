# ADR-0001 — In-memory windowed feature store vs Redis / Flink

**Status:** Accepted
**Date:** 2026-01-15
**Deciders:** self-directed engineering case study

## Context

The scoring path needs sub-50 ms latency and access to sliding-window aggregates for every entity
touched by the transaction (card, customer, device, ip, merchant). Windows are 1 m / 5 m / 1 h / 24 h
/ 7 d. Real-world options:

- **Redis** with sorted sets + a TTL scheme.
- **Apache Flink** with keyed state.
- **In-process** ring buffer per entity, replayable from the event log.

## Decision

Use an **in-process ring-buffered aggregator** (`WindowedFeatureAggregator`) per entity. Slice-width
of 60 s and 10,080 buckets gives 7-day retention. Updates are O(1) amortised; per-request aggregation
is O(bucket-count-in-window) and never touches the database. The state is **rebuildable** from the
transaction log via `FeatureStoreService.Rebuild(transactions)`.

## Consequences

**Positive:**
- No external service required; the whole pipeline runs on a fresh dev box.
- Latency for feature snapshot is dominated by hash-lookup and a small loop — measured p50 well
  under 1 ms.
- Boundary semantics are unit-testable with a `FakeClock` — no clock skew from a network round-trip
  to Redis.
- The `Rebuild()` code path is a first-class operation, not a scary migration script.

**Negative / limits:**
- Feature state is process-local. Horizontal scale-out requires sticky partitioning across nodes
  (or Redis as a store adapter behind `FeatureStoreRuntime`).
- Memory grows with active entity count × slice count. At 10,080 buckets × 100 bytes per bucket ≈
  1 MB per active entity, so 100 K active entities ≈ 100 GB. Mitigated by evicting cold entities
  on the same schedule as bucket eviction (`Advance`).
- Crash recovery requires replay from the persisted transaction log; startup time therefore scales
  with retained transaction volume. This is acceptable at PesaGate-fictional volumes and would be
  revisited if we grew past ~10 million retained events.

## Alternatives considered

- **Redis sorted sets keyed by entity id with score = timestamp, values = amount / merchant JSON**.
  Rejected because it added a required infrastructure dependency and the same-second aggregation
  round-trip cost was on the order of the whole latency budget in early spikes.
- **Flink / Kafka Streams** with keyed windows. Rejected because the operational burden dwarfs the
  problem at portfolio scale; the correctness win is real but the infra cost is not justified until
  we need cross-node replay.

## Related

- `src/FraudPipeline.Domain/Features/WindowedFeatureAggregator.cs`
- `src/FraudPipeline.Application/FeatureStore/FeatureStoreService.cs`
- Tests: `WindowedFeatureAggregatorTests`, `FeatureStoreReplayTests`
