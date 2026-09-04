# ADR-001 — Event time with a bounded watermark

## Status

Accepted.

## Context

Mobile telemetry can be delayed, duplicated and reordered. Processing immediately by arrival time creates false harsh-braking, stop and route events. Waiting indefinitely for every missing sequence prevents progress when a device drops data. Sequence number and device timestamp also answer different questions: sequence expresses producer order; device time estimates when the observation occurred.

## Options

1. Process only by ingest/processing time.
2. Block until every next sequence arrives.
3. Sort a whole trip after it completes.
4. Maintain a per-vehicle bounded event-time buffer and route events behind its watermark to a late sink.

## Decision

Use option 4. For each vehicle, track maximum observed device time and calculate:

`watermark = maxDeviceTimestamp - AllowedLateness`

Events eligible before that watermark are emitted in sequence order. Any new event behind the watermark or at/below the last emitted sequence is hopelessly late and retained in `LateArrivals`. A processing-time quiet timer drains the final buffered events. The default lateness is five seconds and is typed configuration.

## Consequences

- Correctly delayed events within the bound are reordered before stateful rules.
- The pipeline deliberately adds up to the lateness interval.
- Watermark state is isolated per vehicle.
- Late data remains inspectable and can inform tuning or an offline correction workflow.
- Device and ingest timestamps are both retained for lag analysis.

## Risks

- A badly skewed future device clock can advance a watermark too aggressively.
- Sequence resets require a device-session/epoch field in a real protocol.
- In-memory watermark state is lost on process restart.
- An insufficient lateness bound increases late events; an excessive one increases latency and memory.

## Alternatives

A Kafka Streams/Flink event-time window with durable keyed state is the production-scale alternative. Strict sequence blocking was rejected because tunnel dropouts would stall a vehicle indefinitely. Processing time was rejected for stateful safety rules, but remains useful for lag, staleness and quiet-buffer flushing.
