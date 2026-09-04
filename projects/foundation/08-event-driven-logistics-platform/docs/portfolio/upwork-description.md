# Upwork Portfolio Description

**Savanna Event-Driven Logistics Platform — self-directed engineering case study**

**Problem:** Mobile fleet telemetry is duplicated, delayed and reordered, which can corrupt live vehicle state and trigger repetitive false alerts.

**Built:** A .NET 10 fleet reference implementation that ingests high-rate GPS batches, restores bounded event-time order, partitions processing by vehicle, tracks trips/geofences, recalculates ETA and safely replays incidents.

**Engineering focus:** Layered idempotency, backpressure and dead letters, hand-built geospatial indexing, dwell hysteresis, rebuildable projections, alert suppression and OpenTelemetry.

**Stack:** ASP.NET Core minimal APIs, EF Core, SQLite, `System.Threading.Channels`, JWT policies, OpenTelemetry and xUnit.

**Verification:** 58 passing tests. Host-measured synthetic runs include a 10,000-ping API batch, exact grid-vs-brute-force geofence equivalence and ETA error metrics.

This is a self-directed portfolio project, not client work.
