# Runbook — Tenant Quota Breach

## Trigger

- `402` with ProblemDetails type `quota-upgrade` or `upgrade-required`.
- `429` with type `rate-limit`.
- Usage dashboard reaches a soft threshold.

## Triage

1. Capture correlation ID, tenant ID, plan, metric and current period.
2. Query `GET /api/v1/usage` with the tenant token/header.
3. Distinguish monthly capacity (`jobs-created`, assets/users) from requests per minute.
4. Check for retry loops, compromised credentials or legitimate growth.
5. Confirm no other tenant's usage appears in the response.

## Response

### Soft monthly threshold

- Notify the tenant owner through the production channel.
- Recommend workload reduction or a plan change.
- Continue service; the response reports `SoftLimitReached`.

### Hard monthly threshold

- Do not edit counters to bypass commercial controls.
- Preview/approve a plan upgrade, then apply it through the billing flow.
- If usage is erroneous, preserve evidence and correct it through a reviewed administrative tool (not implemented here).

### Per-minute threshold

- Honor `Retry-After`.
- Back off callers and inspect for loops.
- If sustained traffic is legitimate, move to a plan with a higher API request entitlement.

## Period rollover validation

Monthly keys use the first day of the UTC month. The previous row remains historical and a new row starts at zero. `FakeClock` tests cover September-to-October rollover.

## Success criteria

- Requests recover after backoff, rollover or approved upgrade.
- Usage remains tenant-scoped.
- No direct database mutation or cross-tenant counter reset occurred.

## Escalation

Escalate if increments are lost/duplicated, one tenant affects another, a plan change does not update limits, or several nodes disagree. Multi-node disagreement requires the planned distributed atomic meter.
