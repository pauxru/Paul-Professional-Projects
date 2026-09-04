# Portfolio — Upwork description

**Multi-channel notification platform (self-directed case study).**

I designed and built a production-shaped notification service that
delivers to Email, SMS, Push, and Webhook with the reliability primitives
serious SaaS platforms need: pluggable provider simulators, primary →
secondary failover, per-provider circuit breaking with automatic recovery,
retry with jittered exponential backoff, per-tenant fair scheduling,
idempotency, deduplication, quiet hours across timezones, priority lanes,
per-recipient marketing frequency caps, monthly quotas, signed one-click
unsubscribes, signed delivery receipt webhooks with replay protection, and
a dead-letter queue with replay.

Built in .NET 10 as a modular monolith (Domain / Application /
Infrastructure / Api) with EF Core on SQLite, JWT-scoped authorization
policies, OpenTelemetry tracing and metrics, and a lightweight ops
dashboard. Runs entirely on a laptop — no Docker, no external broker, no
cloud dependency.

Verified with 69 automated tests (unit + integration through a real
ASP.NET Core test host) and a small throughput driver whose real numbers
are recorded in the repo. Full architecture, ADRs, security review,
schema, runbooks, and demo script live under `docs/`.

This is a self-directed engineering case study — no client, no real
users, no production traffic — designed to demonstrate exactly the
engineering judgement I would bring to a paid engagement.
