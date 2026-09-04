# Runbook — Failed Invoice Run

## Trigger

- Background log `Background invoice run failed`.
- `billing.invoice_run.duration` stops reporting or rises unexpectedly.
- Due subscriptions advance without expected invoices, or `/api/v1/invoices/run` returns a server error.

## Immediate safety

1. Do **not** delete invoices, lines, sequences, or usage rollups.
2. Pause only the invoice worker deployment if errors are repeating rapidly; API reads and usage ingestion may remain available.
3. Capture the correlation ID, UTC time window, deployment version, database health, and exception class.
4. Confirm `/health/ready`; if database connectivity is unhealthy, restore database service/file access first.

## Diagnose

1. Query due subscriptions ordered by `current_period_end`.
2. Query invoices for the same subscription/period/reason. A row means the unique constraint already established the billing winner.
3. Check plan-version JSON, currency, meter/rollup, pending charges, coupon limits, tax flags, and account credit currency.
4. Check migration history and disk/lock errors.
5. Inspect outbox rows separately; an invoice can be valid even when delivery is pending.

## Recover

1. Correct configuration or append corrective catalogue/credit data; never edit a historical plan version or finalized invoice.
2. Invoke `POST /api/v1/invoices/run` with an administrator JWT and a new `Idempotency-Key`.
3. Re-run is safe: `(subscription, period, billing reason)` is unique.
4. Verify exactly one invoice, closed usage rollup, advanced period, sequential number, and `invoice.created` outbound record.
5. Resume the worker.

## Escalate

Escalate to engineering and finance operations if the unique key is absent but a customer-facing charge exists at a payment provider, totals cannot be reconstructed from stored evidence, or database corruption is suspected.

## Post-incident

Add a regression test for the failing data shape, document affected fictional/real tenant IDs through secure channels, and reconcile invoice/outbox/payment records. Never issue a silent database edit; use a credit note or explicit corrective command.
