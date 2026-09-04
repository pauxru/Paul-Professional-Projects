# Demo Script

1. Explain the problem: CRM contacts need to become ERP customers while both systems can throttle or fail.
2. Run `scripts\demo.ps1`.
3. Show the connector registry and its typed auth, pagination, operation, and contract metadata.
4. Show the seeded flow and explain immutable versions plus activation.
5. Point out that the script configures the ERP simulator to reject one write with 422.
6. Inspect the resulting `PartiallySucceeded` run and its step timeline.
7. Open the DLQ page; show the quarantined record and redacted history.
8. Replay the record. Explain that the original stable idempotency key is reused.
9. Open the mapping test bench and change `upper` to `lower`; inspect field-level trace.
10. Toggle a simulator 429 and explain `Retry-After`, breaker, and metrics.
11. Close with the production boundary: managed identity/vault, durable scheduler leases, server database, OTLP, and vendor contract tests.
