# ADR-002: SQLite bounded store-and-forward at the edge

## Context
Plant WAN links can fail while local equipment continues running. Unbounded memory buffering loses data on process restart and eventually exhausts memory; dropping all telemetry loses diagnostic context.

## Options
1. Drop telemetry while offline.
2. Keep an unbounded in-memory queue.
3. Use a durable SQLite queue with a fixed capacity and oldest-first eviction.

## Decision
Choose option 3. Each queue record contains the device, sequence, received time, and full JSON reading. A unique `(device_id, sequence)` key controls duplicate growth. The gateway replays `id`-ordered batches, deletes only after an accepted-or-duplicate receipt, and caps the queue with explicit oldest-first eviction.

## Consequences
The edge survives process restarts and supports at-least-once replay with exactly-once-effective cloud persistence. Capacity exhaustion has visible, deterministic data-loss behavior instead of hidden growth.

## Risks
Oldest-first eviction can discard data needed for an investigation. A real deployment should make capacity, priority, and disk-health alarms asset-specific.

## Alternatives
RocksDB/file segments, a local message broker, or store-and-forward appliances were not selected because they would add host dependencies and obscure the core recovery invariant.
