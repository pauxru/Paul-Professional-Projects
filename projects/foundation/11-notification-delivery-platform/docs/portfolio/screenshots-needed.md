# Portfolio — Screenshots to capture

Take these when preparing the portfolio page. All screenshots are of the
running local system; nothing has been recorded from production traffic.

1. **Swagger UI** — `http://localhost:5011/swagger`
   - The `/api/v1/notifications` operation expanded.
   - The `/api/v1/webhooks/receipts` operation showing headers and
     schema.
2. **Ops dashboard** — `http://localhost:5011/ops`
   - Queue depth, provider health block, DLQ depth, recent sends table.
3. **A send → sent lifecycle** — split view:
   - The POST request in PowerShell,
   - The notification row in `/api/v1/notifications/{id}` transitioning
     `Queued → Sent → Delivered` after the receipt arrives.
4. **A failover event**:
   - Log lines showing the primary provider's transient failure,
   - The next log line showing the secondary succeed.
5. **The DLQ**:
   - `GET /api/v1/admin/dlq` returning at least one row,
   - The replay endpoint response with `replayed: 1`.
6. **A tests run**:
   - Terminal output of `dotnet test -c Release` with `Passed: 69`.
7. **Diagrams** (already committed under `docs/`, render on GitHub):
   - Pipeline architecture,
   - Send sequence with failover,
   - Notification state machine.

Do not screenshot secrets, tokens, or real personal data — everything in
this project is fictional.
