# Runbook — Deploy

## Trigger

Approved immutable image tag and change record.

## Preconditions

- CI build/tests/SBOM completed.
- Bicep/Terraform change reviewed separately.
- Stable revision name captured.
- Backup/restore posture checked for migration risk.
- Staging evidence and production approval present.

## Procedure

1. Authenticate through GitHub OIDC or approved operator identity.
2. Confirm resource group, app, job, registry and image tag.
3. Update the migration job image.
4. Start one migration execution and wait for `Succeeded`.
5. Verify no pending migrations via candidate readiness.
6. Update the Container App to create a candidate revision.
7. Confirm `/health/startup`, `/health/live` and `/health/ready`.
8. Run `scripts/deploy-canary.ps1`.
9. Observe 5%, 20%, 50% and 100% gate evidence.
10. Confirm queue/DLQ, database load, error rate and p95.
11. Record deployment ID, migration execution, revision and image digest.

## Abort criteria

- Migration failed/timed out.
- Readiness is unhealthy.
- Metrics missing or below sample minimum.
- Error rate or p95 latency breaches threshold.
- Unexpected DB load, DLQ growth or authorization errors.

## Verification

```powershell
.\scripts\smoke.ps1 -BaseUrl "https://APP_FQDN"
az containerapp revision list -g RESOURCE_GROUP -n APP_NAME --output table
```

## Escalation

Follow [rollback.md](rollback.md). If data compatibility is uncertain, stop traffic changes and involve the database owner before any schema action.
