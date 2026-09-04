# Runbook — Rollback

## Trigger

Failed smoke/canary gate, regression, security concern or operator decision.

## Immediate actions

1. Freeze further deployments.
2. Capture stable/candidate revision names and current weights.
3. Route stable to 100%, candidate to 0%.
4. Deactivate candidate after traffic drains.
5. Verify stable readiness and customer path.
6. Preserve candidate logs, traces and migration execution evidence.

```powershell
az containerapp ingress traffic set `
  -g RESOURCE_GROUP -n APP_NAME `
  --revision-weight "STABLE=100" "CANDIDATE=0"
az containerapp revision deactivate `
  -g RESOURCE_GROUP -n APP_NAME `
  --revision CANDIDATE
```

## Database check

- Was only an expand/backfill migration applied? Traffic rollback is normally safe.
- Was the old column dropped? Do not run the old revision.
- Did new code emit externally visible events? Prevent duplicates; do not delete evidence.
- Is data corrupt? Stop writers and initiate the DR restore decision.

## Success criteria

- Stable revision handles 100% traffic.
- `/health/ready` is healthy.
- Error rate/latency recover.
- Queue and DLQ stabilize.
- No incompatible old code is reading contracted schema.

## Communications

Record detection time, rollback decision, traffic restored time, data impact, owners and next update. Targets are in [../rollback.md](../rollback.md).
