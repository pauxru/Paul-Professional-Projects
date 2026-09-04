# Portfolio Summary

## Classification

Self-directed engineering case study and production-style reference implementation. It is not client work and has not operated a real fleet.

## Problem

Fleet pings arrive duplicated, delayed, out of sequence and with imperfect GPS/device clocks. Naive processing produces corrupt current state, false trip transitions and alert storms.

## What was built

A .NET 10 logistics platform for Savanna Logistics Ltd (fictional) with:

- fleet, driver, route, stop, trip and geofence registries;
- 10,000-ping batch ingest and a configurable fault-injecting simulator;
- event-time watermark, per-vehicle channel partitions and backpressure;
- cache/database deduplication with content conflicts;
- rebuildable vehicle-state projection and safe incident replay;
- hand-built haversine, polygon and route-corridor maths plus grid indexing;
- rolling ETA history/accuracy and operational alert rules;
- JWT/policy API, dashboard, Problem Details, health and OpenTelemetry.

## Strongest engineering signals

1. It models bad telemetry explicitly instead of assuming perfect order.
2. It separates source events from disposable projection state.
3. Replay is a supported product workflow and is tested for alert idempotency.
4. Local algorithms map honestly to Kafka/Event Hubs and PostGIS rather than pretending SQLite/channels are distributed.
5. Performance claims are tied to commands and measured synthetic outputs.

## Verification

The final repository build/test transcript is in `docs/test-results.md`. Targeted measured runs recorded:

- 10,000 pings in 3,604.60 ms (2,774.23 pings/s);
- 10,000,000 brute geofence evaluations versus 10,510 indexed candidates;
- synthetic ETA mean absolute error 41.85 seconds, p90 81.25 seconds.

These are host-specific synthetic measurements, not production claims.
