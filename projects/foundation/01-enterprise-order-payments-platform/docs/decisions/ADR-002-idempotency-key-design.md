# ADR-002 — Idempotency-Key semantics and storage

- Status: Accepted
- Date: 2025-11-24
- Deciders: @pauxru (solo)

## Context

Payment platforms cannot afford duplicate side-effects on retried POSTs. A
client that times out has no way to know whether the previous request
mutated state. Stripe's `Idempotency-Key` header solves this in the wider
industry: a client supplies a key; the server guarantees that regardless of
how many times a request with that key arrives, the response and side-effects
are those of the FIRST successful call.

We need the same semantics on `POST /orders`, `POST /payments/authorize`,
`POST /refunds`.

## Options

1. **In-memory dedup** — a `ConcurrentDictionary<string, TaskCompletionSource>`
   in the process. Fast, but loses state on process restart. Cannot survive
   scale-out.
2. **Redis dedup** — SETNX with a TTL. Fast, no state after TTL, requires
   Redis. We do not have Redis on the build host.
3. **Persist to the primary DB** with a UNIQUE index on `(Key, Endpoint)`,
   capture the full response, and replay it byte-for-byte on subsequent hits.
4. **Idempotency at the domain layer only.** Every service checks for prior
   state. Duplicates every use case's guard, and the response of the second
   hit is a fresh view of the aggregate rather than the original response
   body — subtly wrong for `201 Created` vs `409 Conflict`.

## Decision

**We persist idempotency in the primary DB (Option 3).**

- Table `IdempotencyRecords` with columns:
  `Key`, `Endpoint`, `RequestHash`, `ResponseStatus`, `ResponseContentType`,
  `ResponseBody`, `CorrelationId`, `CreatedAtUtc`.
- UNIQUE index on `(Key, Endpoint)`.
- An ASP.NET Core middleware runs late in the pipeline (after auth), computes
  a SHA256 of the raw request body, looks up the record, and replays it
  verbatim if found (with `Idempotent-Replay: true` in the response).
- Same key + different body → `409 Conflict` (`idempotency.conflict`). This is
  the honest signal: the client is retrying a different action under the
  same key by mistake.
- Only 2xx responses are cached — a 4xx failure does not "burn" a key so the
  client can correct the payload and retry with the same key.

## Consequences

- **Positive** — survives process restart; portable across horizontal
  replicas because the store is shared; identical response bytes on replay
  make the contract observable in a `curl -v`.
- **Negative** — every POST does one extra read + one extra write. Mitigated
  by the UNIQUE index on `(Key, Endpoint)` and the endpoint whitelist.
- **Positive** — the entire feature is one middleware and two tests, so
  reviewers can audit it in five minutes.

## Risks

- The middleware buffers the response body to capture it. Very large
  responses could balloon memory; the current whitelist is only small JSON
  responses so this is fine.
- Content-type must be preserved on replay. Middleware records
  `Response.ContentType` and replays it exactly.
- If the DB write of the idempotency record fails after the endpoint has
  already committed its own transaction, we get a fresh execution on retry
  which would re-run the side-effect. Mitigated by placing the record write
  in the same request scope and treating unique-constraint failures as
  benign concurrent duplicates.

## Alternatives revisited

If we scale horizontally with high traffic, we would add an in-front Redis
cache with fallback to the DB. The middleware interface is unchanged.
