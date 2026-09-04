# Project 17 — Distributed Job Scheduler & Orchestrator

> A self-directed engineering case study for the fictional **Northstar Platform Team**, which operates nightly ETL, report-generation, data-export, and cleanup jobs across several worker nodes. All demo data is fictional.

## What it is

This is a .NET 10 job-scheduling and orchestration system built as a modular monolith with a separate worker host. It exposes ASP.NET Core Minimal APIs and a small HTML/JavaScript operations dashboard for defining jobs, inspecting runs and logs, managing workers, replaying dead-lettered work, and viewing leadership.

The central design goal is not merely “run work in the background.” It is to preserve correct behavior when multiple workers race, a process stalls, a lease expires, or a previously stalled process resumes.

## Headline engineering challenge: coordinated work claims without a lock service

Workers coordinate through the shared database rather than a separate distributed lock service:

1. A worker atomically claims a due `Pending` run with a conditional SQL `UPDATE`, recording `leaseOwner`, `leaseToken`, and `leaseExpiresAt`.
2. The same successful claim mints a monotonically increasing **fencing token**.
3. While executing, the worker heartbeats to extend its lease.
4. If heartbeats stop and the lease expires, the elected leader reaps the run back to `Pending`; another worker can claim it with a newer fencing token.
5. Completion is accepted only through the equivalent of:

   ```sql
   WHERE FencingToken = @token AND State = 'Running'
   ```

   A stalled worker that resumes after another worker has reclaimed the run cannot overwrite the newer result. Its stale completion updates zero rows.

This deliberately provides **at-least-once execution**, not an impossible claim of exactly-once execution. Idempotency keys make duplicate external effects manageable.

## What this project demonstrates

- **Distributed-systems reasoning:** lease expiry, failure detection, stale writers, ownership epochs, and split-brain avoidance.
- **Concurrency correctness:** conditional EF Core 10 bulk updates for claims, heartbeats, completion, reaping, singleton jobs, and concurrency gates.
- **Database-backed coordination:** SQLite is the default durable queue/state store, requiring no Docker, Redis, or external service for the demo.
- **Scheduling depth:** a hand-written five- and six-field cron parser with ranges, steps, lists, names, `L`, `#`, and the Vixie day-of-month/day-of-week OR rule.
- **Time correctness:** timezone-aware schedule calculation and explicit treatment of spring-forward skipped local times and fall-back repeated local times.
- **Clean/hexagonal architecture:** `JobScheduler.Domain` has no infrastructure dependencies; `JobScheduler.Application` owns port interfaces; `JobScheduler.Infrastructure` implements them with EF Core; `JobScheduler.Api` and `JobScheduler.Worker` are hosts.
- **Testing under contention:** deterministic clock-driven tests alongside real file-SQLite contention tests exercise expiry, competing claims, and recovery rather than relying only on in-memory substitutes.
- **Operational resilience:** retries and backoff, poison detection, retry budgets/circuit breakers, timeouts, cooperative cancellation, dead-lettering and replay, DAG dependencies, fairness aging, and worker draining/dead-node detection.
- **Observability and security:** OpenTelemetry metrics, correlation IDs carried into run logs, ProblemDetails, pagination, rate limits, JWT scope policies, and an allow-list of fixed handlers instead of arbitrary payload-driven shell/code execution.

## Architecture and delivery scope

The stack is C# on `.NET 10` (`net10.0`), ASP.NET Core Minimal APIs, EF Core 10 with SQLite by default, xUnit, and OpenTelemetry. The API and worker run locally with only the .NET SDK.

The default deployment is intentionally a **single-node SQLite** case study. SQLite serializes writers, which makes the conditional claim protocol easy to inspect and test, but it is not presented as a horizontally scalable production data plane. The case study documents—not implements—the production mapping:

- PostgreSQL can use `SELECT … FOR UPDATE SKIP LOCKED` to distribute candidate selection while retaining lease and fencing checks.
- Redis can support atomic, TTL-based claim operations when paired with durable run history and carefully defined recovery semantics.
- etcd can provide lease/CAS-based coordination, particularly for leadership, while durable job/run state remains a separate concern.

A `Dockerfile` and `docker-compose.yml` are **authored but unverified**: Docker was unavailable on the build host, so the compose stack has not been started or verified.

## Key metrics

- **186** xUnit tests: **155** in `JobScheduler.UnitTests` and **31** in `JobScheduler.IntegrationTests`; **0 failed, 0 skipped**.
- Approximately **~10k lines of C# across 5 projects** (an order-of-magnitude estimate).
