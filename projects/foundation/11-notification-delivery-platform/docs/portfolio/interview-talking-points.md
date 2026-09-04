# Portfolio — Interview talking points

## Why an outbox in SQLite

A notification platform's most valuable property is that no message is
ever lost. The cheapest way to guarantee that on a laptop with only the
.NET SDK installed is to make the queue a table in the primary DB.
Consequences: writes serialise on SQLite (the throughput measurement
lives in `docs/throughput-test.md`), the code paths are identical to
Postgres, and the fairness and DLQ tests can inspect the queue directly
instead of poking a broker.

## Why per-provider circuits, per-tenant fairness

The two failure modes of a delivery pipeline are (a) a bad provider
poisoning every send, and (b) a noisy tenant starving every other tenant.
Two independent blast-radius boundaries handle these:

- **Provider circuit breaker** — per-provider `Closed → Open → HalfOpen`
  state machine in the domain layer. Trips on consecutive failures;
  half-open probes recover automatically. Persisted so it survives
  restart.
- **Fairness scheduler** — `TenantFairnessScheduler.PickBatch` returns a
  weighted round-robin batch per tick, capped per tenant. Tested at unit
  level (fairness property under 3+ tenants) and at pipeline level (one
  process call, both tenants advanced).

## Why write our own template engine

Rendering user-supplied templates is the most common vector for XSS in
notification systems. Handlebars/Fluid/Scriban are all excellent, but I
wanted the escape invariant to be one line I could stare at:
`WebUtility.HtmlEncode`. The engine supports token dotted paths,
conditionals, loops, and a strict mode that rejects unknown tokens. The
`{{raw:x}}` marker exists but requires a template-level opt-in. Every
rule has a unit test.

## Why signed unsubscribe tokens instead of DB tokens

- Stateless verification — no DB write on issue, no cleanup, no lookup on
  redeem.
- Tamper evident by design (`FixedTimeEquals` on HMAC).
- Expiry enforced by the injected clock — easy to unit test.
- Cost: no immediate revocation. If we ever need it, add a revocation
  table checked at redeem — this is scoped to one repository.

## What I'd change to go to production

- Move the outbox to Postgres and add a leased-worker registry so
  multiple worker instances can safely dequeue in parallel.
- Replace the token bucket rate limiter with a distributed one (Redis).
- Add an outbound URL allowlist and private-IP deny list to the webhook
  channel to close the SSRF hole.
- Retain payloads for a bounded window, not indefinitely.
- Add a real key-rotation flow for JWT and webhook signing keys.

## Concrete numbers I can point to

- **69 tests** pass in `dotnet test -c Release`, split 41 unit + 28
  integration.
- **~200 req/s** measured ingestion rate on the single-thread driver
  through the full HTTP + JWT + validation + persistence pipeline on
  in-memory SQLite. Reproducible via `ThroughputDriver`.
- Deterministic tests — every failure mode covered lives under a
  `[Fact]`, so any regression is caught in CI.
