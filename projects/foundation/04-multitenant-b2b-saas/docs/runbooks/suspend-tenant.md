# Runbook — Suspend a Tenant

## Trigger

- Three consecutive signed payment-failure events automatically suspended the tenant.
- Security/contract decision requires an immediate manual suspension.

## Preconditions

- Confirm the organization slug/ID from an authenticated platform-admin session.
- Record an incident/change ticket and correlation ID.
- Do not alter tenant-owned rows directly.

## Procedure

1. Query `/api/v1/admin/tenants` and confirm the target is the intended fictional/demo tenant.
2. Check billing event receipts and audit history for the initiating failures.
3. For automatic dunning, verify `ConsecutivePaymentFailures >= Billing:SuspendAfterFailures`.
4. Confirm tenant requests return `403` with type `tenant-access`.
5. Preserve the database and relevant structured logs; do not delete tenant data.
6. Notify the designated tenant contact through the production communication process (not implemented in this demonstration).

## Reactivation

1. Verify payment recovery outside the application.
2. Deliver a valid, fresh, unique `PaymentSucceeded` billing event.
3. Confirm status becomes `Active` and consecutive failures reset to zero.
4. Acquire a new token and verify `/api/v1/usage` succeeds.
5. Record the recovery correlation ID.

## Validation

```powershell
# Platform-admin token/header variables are assumed.
Invoke-RestMethod http://localhost:5004/api/v1/admin/tenants -Headers $headers
```

Expected: the target moves `PastDue -> Suspended` after the configured threshold, and `PaymentSucceeded -> Active`.

## Escalation

Escalate if the signed event is accepted but status does not change, if another tenant changes, or if suspended-tenant requests still succeed. Treat any cross-tenant symptom as a severity-one isolation incident.
