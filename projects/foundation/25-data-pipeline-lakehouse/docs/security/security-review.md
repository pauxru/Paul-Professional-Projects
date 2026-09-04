# Security Review — Data Pipeline & Analytics Lakehouse

> Self-directed engineering case study. This review covers the threats that matter for a data platform
> with a SQL query API over analytical marts. It is written honestly: the section **"What this review
> does NOT claim"** is as important as the findings.

## Scope & trust boundaries

```
[ Client / browser ]  --HTTP-->  [ Lakehouse API :5025 ]  --in-proc-->  [ SQLite serving DB (gold only) ]
                                        |                                  ^
                                        | in-proc                          | rebuild (gold tables only)
                                        v                                  |
                                 [ Filesystem lake: bronze/silver/gold + quarantine + checkpoints ]
```

- **Untrusted:** HTTP request bodies, the ad-hoc SQL string, JWTs presented by callers, query params.
- **Trusted (this host):** the filesystem lake, configuration, the signing key at rest.
- **Hard boundary:** PII (names, emails) exists only in **bronze/silver on the filesystem**. Only
  **gold serving tables** are projected into the SQLite query surface. Client SQL therefore *cannot
  reach PII*, because it is not present in the queryable store.

## STRIDE analysis

| # | Category | Threat | Mitigation (in code) | Residual risk |
|---|---|---|---|---|
| S1 | **Spoofing** | Caller impersonates a user/role | JWT bearer auth (HS256); `reader` / `operator` roles enforced by ASP.NET Core authorization policies (`RequireAuthenticatedUser` + `RequireRole`). No token => 401; wrong role => 403 (both tested) | Dev signing key is symmetric and in config — acceptable only for local/dev; production must use a real IdP (see non-claims) |
| T1 | **Tampering** | Client mutates data via the SQL API (`UPDATE`/`DELETE`/`DROP`/DDL) | `SqlGuard` rejects any non-`SELECT`/`WITH` statement and a forbidden-keyword allow-list; **and** the engine uses a genuinely **read-only** SQLite connection; writes/DDL return 400 (tested) | A SQLite read-only connection is the backstop even if the guard is bypassed |
| T2 | **Tampering** | Bronze/immutable data altered | Bronze is append-only in the table format; commits are new snapshots, never in-place edits; readers pin snapshots | Filesystem-level tampering by a host user is out of scope (OS responsibility) |
| R1 | **Repudiation** | "I didn't run that" / no audit trail | Every DAG run is persisted to run history with run id, per-task timings, row counts and outcome; structured logs (Serilog) carry run + task ids; OpenTelemetry span per task | Logs are local; no tamper-evident/centralised audit store in this build |
| I1 | **Information disclosure** | Client reads PII (names/emails) through the query API | PII never enters the serving store — only `GoldServingTables` are loaded into SQLite; an integration test asserts no `bronze_`/`silver_` tables are present | Gold could carry PII if a future dimension exposes it; keep gold PII-minimal / tokenised |
| I2 | **Information disclosure** | SQL injection / statement batching to exfiltrate or pivot | `SqlGuard` blocks comments, `;` batching, and non-SELECT starts; the **metrics layer** never accepts raw SQL (named metrics + allow-listed dimensions + closed `TimeGrain` enum); parameterised data loading | Novel injection vectors — mitigated by defence in depth (guard + read-only + metrics allow-list) |
| I3 | **Information disclosure** | Verbose errors leak internals | Problem-details style responses; guard rejections return a terse reason; detailed errors only in Development | — |
| D1 | **Denial of service** | Expensive/huge query exhausts resources | Per-statement **timeout** and a **row cap** (default 1000, max 5000) on every query; the serving DB is small (gold marts only) | No global rate limiter wired in this build (documented as future work; .NET has built-in rate limiting) |
| D2 | **Denial of service** | Overlapping/duplicate pipeline runs corrupt state or thrash | DAG runner enforces single-writer-per-window; overlapping runs of the same window are rejected (`OverlappingRunException` => 409) | — |
| E1 | **Elevation of privilege** | `reader` performs `operator`-only actions (run/rerun/backfill) | Operator endpoints require the `operator` role; a reader token is 403 (tested). Operator tokens additionally carry `reader` so they satisfy read policies | Dev token endpoint issues tokens without real identity — dev/test only |

## PII in bronze and the redaction/tokenisation strategy

- **Where PII is:** `bronze_customers` / `silver_customers` hold `name` and `email` (synthetic, e.g.
  `user{i}@example.com`). These live on the filesystem lake only.
- **Why the query API is safe:** the serving SQLite DB is rebuilt from **gold** tables only. `dim_customer`
  carries business attributes needed for analytics (segment, city, country, loyalty tier) but the query
  surface is analytical, and bronze/silver are never loaded — so raw PII is not queryable via `/api/sql`.
- **Strategy for production (documented, not all implemented here):** tokenise/pseudonymise direct
  identifiers on the way into bronze (hash email to a stable token), keep the mapping in a restricted
  vault, and expose only tokens/derived attributes in gold; apply column-level access control and
  masking in the warehouse (Synapse/Purview). This build demonstrates the **boundary** (gold-only
  serving) rather than a full tokenisation vault.

## Quarantine access

- Rejected rows are written to a `quarantine` table with the reason and the raw payload for triage.
  Quarantine may contain PII (it holds raw source payloads), so it lives on the filesystem lake and is
  **never** projected into the serving store — it is not reachable through the query API. Operational
  access is via the filesystem/runbooks, not the public API.

## Secrets & configuration

- The JWT signing key in `appsettings.json` is a **dev-only** placeholder and is clearly labelled. `.env`
  is gitignored; only `.env.example` (no real secrets) is committed. No real credentials, tokens, or
  personal data exist anywhere in the repository.

## What this review does NOT claim

- **No penetration test or third-party audit** was performed.
- **No production identity provider** — auth is a local HS256 dev token endpoint; a real deployment must
  integrate an IdP (Entra ID / OAuth2) and remove the dev token endpoint.
- **No TLS/at-rest encryption** is configured in this local build (the platform would inherit these from
  the hosting/storage layer in Azure).
- **No global rate limiting / WAF** is wired (documented as future work).
- **No formal PII tokenisation vault** — the build demonstrates the serving-boundary control, not a full
  data-privacy programme.
- **Docker/compose are unverified** (no Docker on the build host), so no container-hardening claims are made.
