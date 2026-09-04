# ADR-002 — Partition event processing by vehicle ID

## Status

Accepted.

## Context

Harsh braking, dwell, trip transitions and current-state projection depend on ordered history for one vehicle. Serializing the entire fleet would preserve order but waste available cores. Running every event independently would allow two pings for one vehicle to race.

## Options

1. One global consumer.
2. One unbounded queue/task per vehicle.
3. A fixed set of bounded channels selected by `vehicleId`.
4. Parallel tasks with a distributed lock per event.

## Decision

Hash the canonical `vehicleId` into a configurable fixed partition count. Each partition is a bounded `Channel<T>` with one reader and multiple writers. Different partitions execute concurrently; all events assigned to one partition execute sequentially. Publishers use `WriteAsync` for backpressure, while `TryPublish` exposes immediate capacity.

## Consequences

- Per-vehicle order is preserved without a lock on every rule.
- A fixed channel count bounds tasks and memory.
- Consumer lag and dead-letter totals are measurable.
- A slow vehicle can delay other vehicles sharing its partition, but not the entire fleet.
- Changing partition count remaps keys and therefore requires a controlled drain/restart.

## Risks

- A hot vehicle can create partition skew.
- Process failure loses queued in-memory messages already acknowledged by the HTTP caller, although source pings remain in SQLite for replay.
- Hash implementations must remain stable within a running deployment.

## Alternatives

Kafka/Event Hubs keyed partitions provide durable queues, retention and consumer groups. One queue per vehicle was rejected because fleet cardinality can be large and dynamic. A global queue was rejected for avoidable head-of-line blocking. Distributed locks were rejected because partition ownership is simpler and cheaper.
