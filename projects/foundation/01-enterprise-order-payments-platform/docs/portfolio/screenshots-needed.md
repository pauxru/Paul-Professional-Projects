# Screenshots needed

This project runs locally against SQLite; there is no hosted demo. A
recruiter or reviewer who wants visuals will want the following captures.
None are yet taken because the project has never been externally hosted.

## Terminal — the money shot

- Full `dotnet test -c Release` output showing `Passed! 84, Failed! 0`.
- `dotnet build -c Release` showing `Build succeeded. 0 Error(s)`.

## Swagger / OpenAPI

- Screenshot of the `GET /openapi/v1.json` output rendered by any OpenAPI UI
  (Swagger UI, ReDoc, Insomnia, Bruno). Show at least the two paths
  `POST /api/v1/orders` and `POST /api/v1/webhooks/payments` with their
  request bodies and responses.

## The idempotency invariant (must-have)

- `curl -v` (or `Invoke-WebRequest`) POST to `/api/v1/orders` twice with the
  same `Idempotency-Key`. Screenshot showing:
  1. The two responses are byte-identical.
  2. The second response's headers include `Idempotent-Replay: true`.
  3. The `X-Correlation-Id` differs between the two calls.

## The webhook flow

- POST a webhook with a real HMAC. Screenshot the 200.
- POST a webhook with a bad HMAC. Screenshot the 401 ProblemDetails body.
- POST the same real HMAC twice. Screenshot the second response containing
  `"replay": true`.

## The reconciliation report

- Upload a synthetic CSV via `POST /api/v1/reconciliation/runs`. Screenshot
  the resulting JSON, highlighting the `matchedCount`,
  `amountMismatchCount`, `missingInProviderCount`, etc.

## The domain state machine

- A whiteboard or Mermaid render of the `Order` transition diagram
  (`Draft → Pending → AwaitingPayment → Paid → Fulfilled | Cancelled |
  Refunded | PartiallyRefunded`). The README embeds this as Mermaid but a
  static PNG makes for easier LinkedIn sharing.

## The failure paths

- `POST /api/v1/refunds` with an amount exceeding the captured — screenshot
  the 422 ProblemDetails body.
- `POST /api/v1/orders` unauthenticated — screenshot the 401.
- `POST /api/v1/orders` with the wrong scope — screenshot the 403.

## Observability

- Console output showing an OpenTelemetry activity span for `order.place`
  including its correlation id.
- Any `outbox_dead_lettered_total` metric emission (force it by pointing
  the dispatcher at a throwing bus for a moment).

Once collected, drop into `docs/portfolio/media/` and reference from the
README's "Example Usage" section.
