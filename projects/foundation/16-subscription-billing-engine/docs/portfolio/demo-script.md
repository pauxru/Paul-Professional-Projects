# Demo Script

## Goal

Show a fictional SaaS account subscribing, reporting usage, upgrading mid-cycle, generating exactly one invoice, failing payment into dunning, and recovering through a signed payment webhook.

## Preparation

1. Run `dotnet run --project src\SubscriptionBilling.Api`.
2. In a second PowerShell terminal run `scripts\demo.ps1`.
3. Keep Swagger UI (`http://localhost:5016/docs`) and logs visible.

## Talking track

1. **Local identity:** obtain a Development-only administrator JWT; Production refuses the demo key.
2. **Immutable version:** append a new price to seeded `API Scale`; the old version remains queryable.
3. **Anchor:** create a subscription with a period starting one month ago.
4. **Metering:** post `12,000` requests; replay the event ID to show idempotency.
5. **Preview/change:** calculate and persist credit plus charge at the exact change instant.
6. **Invoice run:** manually trigger twice with different request idempotency keys and show one invoice for the subscription period.
7. **Collection failure:** use `pm_insufficient`; invoice stays Open and subscription becomes PastDue with a day 1/3/5/7 dunning case.
8. **Recovery:** send an HMAC-signed `payment.succeeded` body with a fresh nonce; invoice becomes Paid and access is restored.
9. **Evidence:** render HTML, inspect correlation IDs, and point to audit/outbound rows and automated tests.

## Important honesty note

The script uses deterministic local adapters and fictional Savanna Logistics data. It does not contact a payment processor, tax service, email provider, or real webhook destination.
