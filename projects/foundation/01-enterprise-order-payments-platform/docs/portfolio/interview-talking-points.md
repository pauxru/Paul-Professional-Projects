# Interview talking points

Pick 2–3 for a 30-minute conversation; pick 5–7 for an hour.

## 1. Idempotency is one middleware, one UNIQUE index, and one test

- Middleware: `src/Contoso.Payments.Api/Middleware/IdempotencyMiddleware.cs`.
- Storage: `IdempotencyRecords` with UNIQUE `(Key, Endpoint)`.
- The invariant is enforced regardless of which endpoint is on the wire —
  the middleware wraps every whitelisted POST.
- Anything you can't get right in five lines of framework middleware, you
  probably can't get right when the on-call pager fires.

*Show:* the middleware, then run
`PaymentFlowTests.Authorize_same_key_twice_only_charges_once`.

## 2. Transactional outbox: atomic state + event, then relay

- The `OutboxWriter` adds `OutboxMessage` rows to the DbContext. The
  service commits its own transaction — state + outbox are atomic.
- The `OutboxDispatcher` (`BackgroundService`) polls, publishes via
  `IEventBus`, retries with exponential backoff + jitter, and dead-letters
  after `MaxAttempts`. Metrics: `outbox_dead_lettered_total`.

*Show:* `OutboxDispatcher.cs`. Run
`OutboxDispatcherTests.Failing_message_is_dead_lettered_after_MaxAttempts`.

## 3. HMAC over raw body + replay guard = webhook auth done right

- Signature: `HmacSha256WebhookSignatureVerifier`. `FixedTimeEquals`
  compare. Timestamp tolerance.
- Replay guard: `WebhookReplayRecords` keyed by SHA256(signature header).
  A replay returns 200 + `replay: true` and does not mutate.

*Show:* the verifier and the `WebhookService.ProcessAsync` order
(verify → replay-check → parse → state advance).

## 4. State machines belong in the aggregate

- `Order.MarkPending`, `MarkAwaitingPayment`, `MarkPaid`, `Cancel`,
  `Refund` — each guarded by the current status; illegal transitions throw
  `DomainException`.
- Unit tests cover the entire transition matrix
  (`OrderStateMachineTests`).

## 5. Reconciliation: the tests that make sure money isn't lost

- `SettlementFileGenerator.Build(rows, MismatchFlags.X)` injects any
  defect class on demand.
- `ReconciliationService` detects: matched, missing-in-provider,
  missing-internally, amount-mismatch, duplicate, status-mismatch.
- Every kind has an integration test.

## 6. Zero-external-infrastructure was a design constraint

- SQLite default; Postgres/Redis/RabbitMQ as configuration-selected
  adapters. Docker labelled UNVERIFIED.
- The invariant this enforces: `dotnet build` and `dotnet test` succeed on
  a fresh clone with no ceremony. That is the difference between "runs on
  the reviewer's laptop" and "doesn't."

## 7. Global EF conventions kept the code honest

- `ValueGeneratedNever` on every Guid PK so EF trusts domain-supplied ids.
- `DateTimeOffsetToBinaryConverter` globally so
  `WHERE NextAttemptAtUtc <= now` translates on SQLite.
- `AutoInclude` on `Order.Lines` and `InventoryItem.Reservations` so
  services never need to remember `Include()` for correctness-critical
  calculations.

## 8. The parallel-reservation test

- 20 concurrent orders against 5 units of stock, driven by
  `Parallel.ForEachAsync` through the real HTTP surface.
- Assertion: successes ≤ 5. No oversell, ever. Because SQLite is
  single-writer, we also verify at least one succeeded (rather than
  everyone racing to failure).

## 9. Failure translation into ProblemDetails

- Domain violation → 422 with a `type` URI + `title` = domain error code +
  `traceId` = correlation id.
- Idempotency conflict → 409 `idempotency.conflict`.
- DB conflict / SQLite contention → 409 `db.conflict` / `db.busy` — the
  client is told it's retryable rather than seeing a 500.

## 10. The honesty discipline

- The README's "Non-claims" and "Known Limitations" sections are as
  detailed as the "Executive Summary." No invented users, no invented
  uptime, no invented certifications.
- Docker compose file exists but is labelled UNVERIFIED because the build
  host cannot start containers.

## 11. What I would build next

- Wire the RabbitMQ adapter and load-test it.
- Postgres provider — verified.
- A contract test against a real Stripe / mPesa payload shape.
- Kubernetes deployment + Prometheus / Grafana dashboards.
- Chaos-engineering test that randomly kills the dispatcher mid-batch and
  asserts no message is lost, no message is duplicated *and* dispatched
  more than the subscriber's dedup window allows.
