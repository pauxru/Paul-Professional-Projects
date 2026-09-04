# Distributed Job Scheduler & Orchestrator

Reliable scheduled and background job execution across multiple workers, with no separate distributed lock service becoming a single point of failure in the work-claiming path. Workers atomically claim durable work, heartbeat their leases, and safely recover work from a failed peer.

## Capabilities

- Atomically claims due work across worker nodes using leases, lease tokens, and monotonic fencing tokens.
- Reclaims expired work automatically; stale workers cannot overwrite a newer owner's completion.
- Schedules manual, interval, and cron-triggered jobs with timezone and DST-aware calculation.
- Supports retries, fixed/exponential/jittered backoff, timeout and cooperative cancellation, retry budgets, circuit breaking, dead-lettering, and replay.
- Provides global, per-definition, and per-queue concurrency controls; singleton jobs; priority scheduling with aging; and DAG dependency chains with cycle detection.
- Registers worker nodes with heartbeats, tags/capabilities, drain support, and dead-node detection.
- Exposes REST endpoints, OpenAPI, an operational HTML dashboard, execution history, run logs, correlation IDs, pagination, rate limiting, and OpenTelemetry metrics.
- Uses JWT bearer scopes and a fixed allow-list of handlers (`report-generator`, `csv-transform`, `cleanup`, `flaky`, `slow`) so job payloads cannot execute arbitrary shell commands or code.

## Built with

- .NET 10 (`net10.0`) and C#
- ASP.NET Core Minimal APIs
- EF Core 10 with SQLite by default
- xUnit
- OpenTelemetry
- Clean/hexagonal modular-monolith architecture with a separate worker console host

*This is a self-directed engineering case study for the fictional Northstar Platform Team; all demo data is fictional.*

