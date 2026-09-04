# ADR-0003 — Partitioning strategy for per-entity ordering

**Status:** Accepted
**Date:** 2026-01-15

## Context

The feature store depends on **causal ordering** of transactions per entity. If a card's Nth
transaction is observed before its (N-1)th, the "1 m velocity" aggregate is wrong, an
impossible-travel rule fires spuriously, and the whole scoring path becomes non-deterministic.

Fan-out ingestion via `System.Threading.Channels` gives us parallelism cheaply, but a naive
round-robin dispatcher shuffles order for concurrent producers.

## Decision

Use a **deterministic FNV-1a hash of `CardId` modulo partition count** as the partition selector
(`PartitionedTransactionBus.PartitionOf`). Each partition has:

- A **bounded, single-reader** channel — so within one partition, order is preserved.
- One dedicated consumer worker (`TransactionConsumer.RunPartitionAsync`).

Because the hash is deterministic and pure, the same `CardId` always lands in the same partition,
whether the producer is the ingest API, a batch replay, or a test.

## Consequences

**Positive:**
- Per-entity ordering is preserved even under concurrent multi-producer ingest. Tested with
  `PartitionedBusTests.PerEntityOrdering_UnderParallelIngestion_IsPreserved` (100 concurrent writes).
- Replay is deterministic — replaying the log through the same partition count reproduces the same
  observation order.
- No coordination between producers is required; the channel is the only synchronisation primitive.

**Negative:**
- **Uneven partition load** for a hot card. If one card produces disproportionate traffic, its
  partition is overloaded. In real production this is a well-known problem addressed with
  sub-partitioning (`CardId` + microsecond slice), but at portfolio-scope traffic it does not bite.
- Changing the partition count invalidates the mapping. In production this would be handled with a
  consistent-hash ring; here we accept that a partition-count change requires drainage.

## Alternatives considered

- **Round-robin dispatch**. Rejected — trivially violates per-entity ordering.
- **Global lock around scoring**. Rejected — collapses the whole point of parallelism.
- **A distributed log (Kafka)**. Fits the same partition-by-key pattern, but is an infra dependency
  we deliberately avoided.

## Related

- `src/FraudPipeline.Application/Ingestion/PartitionedTransactionBus.cs`
- `src/FraudPipeline.Application/Ingestion/TransactionConsumer.cs`
- Tests: `PartitionedBusTests`.
