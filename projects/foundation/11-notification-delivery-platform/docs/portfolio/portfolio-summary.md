# Portfolio — Executive summary

**Project 11: High-Scale Notification Delivery Platform.** A multi-tenant,
multi-channel notification service — Email, SMS, Push, Webhook — with the
reliability primitives every serious platform ends up needing: provider
failover, circuit breaking, retry with backoff, per-tenant fairness,
idempotency, dedup, quota, quiet hours, suppressions, signed unsubscribes,
signed delivery receipts, and a dead-letter queue with replay.

Built from scratch in .NET 10 as a self-directed engineering case study.
Runs entirely on the local host — no Docker, no external broker, no cloud.
SQLite by default; every persistence dependency is behind an EF Core
abstraction that could point at Postgres in a real deployment.

Verified: 69 tests pass in Release under `dotnet test`. The build is
green with `dotnet build -c Release`. Real throughput numbers are in
`docs/throughput-test.md`.
