# Upwork portfolio description

## Title

**Enterprise Order & Payments Platform (.NET 10, self-directed case study)**

## Short pitch (100 words)

A production-shaped .NET 10 ordering + payments platform demonstrating the
reliability engineering real payment systems need: `Idempotency-Key`
middleware backed by a UNIQUE index (no double charges), a transactional
outbox with retry + jitter + dead-letter table (no lost events), HMAC-SHA256
webhook verification with replay protection (no spoofed captures), a domain
state machine with unit-tested transitions (no illegal moves), and provider
settlement reconciliation with detection of every mismatch class. 84
automated tests all pass on `dotnet test -c Release` with zero external
infrastructure.

## Long description (~500 words)

I built this project end-to-end as a portfolio piece — no team, no
customers, no live vendor integration. The goal was to demonstrate the
patterns and judgement that separate senior back-end engineering from
"wired-up CRUD."

### Reliability engineering

- **Idempotent APIs.** `POST /orders`, `POST /payments/authorize`, and
  `POST /refunds` all require an `Idempotency-Key` header. A middleware
  computes a SHA-256 of the request body and stores the full response
  under a UNIQUE `(Key, Endpoint)` index. A retry with the same key + body
  replays the original response byte-for-byte with an `Idempotent-Replay:
  true` header. Same key with a different body returns `409 Conflict`.

- **Transactional outbox.** Every state-changing domain method raises a
  domain event; those events are written to an `OutboxMessages` table in
  the SAME EF Core transaction as the aggregate change. A
  `BackgroundService` polls the table and publishes at-least-once via an
  `IEventBus` port (default in-process, RabbitMQ + Azure Service Bus
  adapters as configuration stubs). Failures back off exponentially with
  jitter and dead-letter after `MaxAttempts`.

- **HMAC-signed webhooks.** The provider's callback endpoint uses
  `HmacSha256WebhookSignatureVerifier` — signature over the raw body,
  timestamp tolerance window, `CryptographicOperations.FixedTimeEquals`
  compare, and a persisted replay guard hashed by the signature header
  itself. Invalid → 401 and nothing mutates.

- **Domain state machine.** The `Order` aggregate has explicit
  `MarkPending`, `MarkAwaitingPayment`, `MarkPaid`, `Cancel`, `Refund`
  methods, each with an explicit legal-predecessor guard. Illegal
  transitions throw `DomainException`. The transition matrix is
  exhaustively unit-tested.

- **Reconciliation.** The `ReconciliationService` imports a provider
  settlement CSV and compares it against internal `PaymentIntents`,
  detecting matched, missing-in-provider, missing-internally,
  amount-mismatch, duplicate, and status-mismatch. A synthetic file
  generator can inject each defect class deterministically for testing.

### Testing

- 53 unit tests (pure domain + application logic) plus 31 integration
  tests (end-to-end through the HTTP surface via
  `WebApplicationFactory<Program>` against a shared-cache SQLite `:memory:`
  DB). All 84 pass on `dotnet test -c Release`. Plain xUnit `Assert.*` —
  no FluentAssertions.

### Documentation

- 21-section README with Mermaid container + sequence diagrams.
- 5 ADRs (outbox vs 2PC; idempotency-key design; SQLite-default with
  Postgres adapter; state machine in domain; at-least-once + dedup).
- STRIDE-based security review with explicit non-claims.
- Full database schema doc with ER Mermaid diagram + index list.
- 3 operational runbooks (stuck payment, outbox backlog, reconciliation
  mismatch).

### What is intentionally *not* claimed

- No real vendor integration. No live traffic. No PCI attestation. Docker
  compose file committed but explicitly labelled UNVERIFIED.

## Skills demonstrated

C#, .NET 10, ASP.NET Core minimal APIs, EF Core, SQLite, xUnit, Polly,
OpenTelemetry, JWT, HMAC, transactional outbox, domain-driven design,
idempotency, webhook security, reconciliation, ADR-driven decision-making,
runbook-driven ops thinking.
