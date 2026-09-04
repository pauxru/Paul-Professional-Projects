# Security Review

Scope: the Distributed Job Scheduler & Orchestrator (Project 17), a self-directed engineering case
study. This review is an honest threat model of the codebase as built. It is **not** a certification
and makes no compliance claims.

## Explicit non-claims (honesty first)

- This is **not** a penetration-tested or independently audited system. No formal assurance is
  implied.
- The bundled JWT token endpoint (`POST /api/v1/auth/token`) is a **development convenience** for
  demos and tests. It is disabled in `Production`, and the app **refuses to start in Production with
  the default signing key**. It is **not** an identity provider — production must integrate a real
  IdP (Entra ID, Auth0, Keycloak).
- The default signing key in `appsettings.json` is a **non-secret placeholder**, clearly labelled,
  and must be overridden via configuration/environment in any real deployment.
- Transport security (TLS) is terminated by the host/reverse proxy; `UseHttpsRedirection` is
  intentionally omitted for the local test host (per the build spec) and is a deployment concern.
- No real personal data, secrets, clients, or traffic figures appear anywhere; demo data is
  fictional (`Northstar Platform Team (fictional)`).

## Trust boundaries

1. **Client → API.** Untrusted HTTP callers. Guarded by JWT bearer auth + scope policies, rate
   limiting, ProblemDetails, and input validation.
2. **API/Worker → Store.** Trusted process → SQLite/Postgres. Coordination integrity enforced by the
   lease/fencing protocol.
3. **Payload → Handler.** The critical boundary: a job payload is **data**, and can only select a
   handler from a fixed **allow-list** — never arbitrary code.

---

## The headline security property: no arbitrary code execution from payloads

A job definition names a `HandlerType` and carries a JSON `PayloadJson`. This is the classic
injection surface for a job system ("run this shell command", "load this assembly"). The design
forecloses it:

- `IHandlerRegistry` is an **allow-list** of registered `IJobHandler` implementations
  (`report-generator`, `csv-transform`, `cleanup`, `flaky`, `slow`). A definition whose handler type
  is not registered is **rejected at create time** (HTTP 400) and can never execute
  (`HandlerNotRegisteredException`).
- A payload is only ever `System.Text.Json`-parsed **data** handed to a handler. There is **no**
  `Process.Start`, no shell, no `eval`, no dynamic assembly/type loading, and no reflection-based
  dispatch driven by payload content anywhere in the execution path.
- Payloads are validated as well-formed JSON objects on create and trigger (`400` otherwise).

This is asserted by tests: `ApiTests.Creating_a_job_with_an_unregistered_handler_is_rejected_400`
sends a handler type of `"rm -rf /"` and expects `400`.

---

## STRIDE analysis

| Threat | Vector | Mitigation in code | Residual risk |
|---|---|---|---|
| **Spoofing** | Forged caller identity | JWT bearer with validated issuer, audience, lifetime, and HMAC signature (`AuthSetup`); `MapInboundClaims=false` to avoid claim-name surprises | Depends on IdP + key management in production; dev token endpoint must stay disabled in prod (enforced) |
| **Tampering** | Concurrent/stale writes corrupting run state | Lease token + **monotonic fencing token**; every coordination write is a guarded CAS (`WHERE FencingToken=@t AND State='Running'`); optimistic `Version` column | Clock-skew tuning of leases (see ADR-001) |
| **Tampering** | Malicious payload altering execution | Handler **allow-list**; payload is inert JSON data; no code/shell execution | A registered handler with its own injection bug (handler-author responsibility) |
| **Repudiation** | "I didn't trigger that job" | Correlation ids propagated into per-run structured `RunLogs`; attempt history, node id, lease owner recorded per run | Logs are not cryptographically signed |
| **Information disclosure** | Log data exposure (payloads/PII in logs) | Logs are structured and scoped; payloads are **not** auto-dumped into logs; read access requires `jobs:read`; error messages are handler-controlled | A careless handler could log sensitive payload fields — documented as a handler obligation |
| **Information disclosure** | Reading others' jobs/runs | All read endpoints require `jobs:read`; no cross-tenant model is claimed (single-tenant scope) | Multi-tenant isolation is out of scope |
| **Denial of service** | Schedule flooding / trigger storm | API **rate limiting** (fixed-window per IP); `jobs:trigger` scope required to trigger; misfire `MaxCatchUp` cap; per-definition **concurrency limits**; per-definition **circuit breaker / retry budget**; priority **aging** prevents starvation | A high global trigger budget still bounded by worker capacity; see `schedule-storm.md` |
| **Denial of service** | Poison job consuming the fleet | `MaxAttempts` + poison detection → dead-letter; circuit breaker opens after N consecutive failures for a cooldown | Tuning of thresholds per workload |
| **Elevation of privilege** | Low-privilege caller performing admin actions | Four scopes with least privilege: `jobs:read` (view), `jobs:trigger` (trigger/cancel), `jobs:manage` (CRUD + DLQ replay), `jobs:admin`; each endpoint pins the minimum policy | Scope assignment is the IdP's responsibility |

---

## Privilege model for triggering & managing jobs

Triggering a job is a **privileged action** (it causes work, and work costs resources), so it is
gated separately from reading:

| Capability | Required scope | Endpoints |
|---|---|---|
| View definitions, runs, logs, workers, DLQ, leader, schedule | `jobs:read` | `GET` endpoints |
| Trigger a run, cancel a run | `jobs:trigger` | `POST /jobs/{id}/trigger`, `POST /runs/{id}/cancel` |
| Create/update/enable/disable/delete definitions, replay DLQ | `jobs:manage` | `POST/PUT/DELETE /jobs/*`, `POST /dlq/{id}/replay` |
| Reserved administrative superset | `jobs:admin` | (accepted by all policies) |

Policies are additive-by-privilege (`manage`/`admin` satisfy `read`; `admin` satisfies all).
Unauthenticated calls to guarded endpoints return **401**; authenticated calls with an insufficient
scope return **403**. Both are covered by `ApiTests` (401 unauthenticated, 403 read-scope-creating,
403 trigger-scope-managing).

---

## DoS via schedule flooding — deeper look

A scheduler is uniquely exposed to self-inflicted DoS (a cron `* * * * *` on a heavy job, a
`RunAllMissed` catch-up after downtime, or a flood of manual triggers). Layered defences:

1. **Ingress:** per-IP fixed-window rate limiter (`AddRateLimiter`, 429 on breach) and the
   `jobs:trigger` scope gate.
2. **Materialisation:** `MaxCatchUp` caps how many missed occurrences a single misfire can enqueue;
   `CatchUpWindowSeconds` bounds the look-back.
3. **Execution:** global + per-definition + per-queue concurrency caps; the per-definition circuit
   breaker stops a failing/expensive type from monopolising workers; priority **aging** guarantees
   low-priority jobs still progress.
4. **Idempotency:** `UNIQUE(IdempotencyKey)` prevents a double-materialised occurrence from
   duplicating work.

Runbook: [`docs/runbooks/schedule-storm.md`](../runbooks/schedule-storm.md).

---

## Secrets & configuration hygiene

- Only `.env.example` is committed; `.env` is gitignored. No real secrets in the repo.
- The signing key, connection string, and node identity are all configuration-driven.
- Startup guard (`GuardSigningKey`) fails fast in Production on the default key.

## Recommendations for a real deployment

- Replace the dev token endpoint with a real OIDC/JWT IdP; rotate signing keys; enforce short token
  lifetimes.
- Terminate TLS at the edge; enable HSTS.
- Move to Postgres with `SELECT … FOR UPDATE SKIP LOCKED`, server-side `now()` for lease clocks, and
  migrations.
- Add per-tenant authorization if multi-tenant.
- Ship logs/metrics/traces to a backend (OTLP exporter) with PII scrubbing and access control.
- Review each handler for its own injection/idempotency properties before enabling it.
