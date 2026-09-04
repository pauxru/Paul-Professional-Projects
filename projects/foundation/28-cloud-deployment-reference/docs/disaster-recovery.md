# Disaster Recovery

## Targets

These are proposed service objectives, not measured guarantees:

| Capability | RPO | RTO |
|---|---:|---:|
| Single instance/revision failure | 0 | 5 minutes |
| PostgreSQL zonal failure (prod HA) | Near-zero | 15 minutes |
| Regional service failure | 15 minutes | 4 hours |
| Accidental logical corruption | Backup point selected by incident command | 4 hours |

## Backup posture

- PostgreSQL production parameters request 35-day retention and geo-redundant backup.
- Key Vault soft delete and purge protection are enabled in production.
- ACR images are immutable release inputs; retain known-good digests.
- IaC and configuration metadata are source controlled; secret values are not.
- Service Bus is not a backup. DLQ retention/replay procedures protect failed events.

## Restore procedure

1. Declare an incident, identify incident commander and freeze deployments.
2. Stop or scale down writers if corruption is active.
3. Select a restore point before the bad write/migration.
4. Restore PostgreSQL to a new server; never overwrite the only copy.
5. Run consistency queries for products, orders, order items, outbox and checkpoints.
6. Update a new Key Vault secret version with the restored endpoint.
7. Run migration status in inspection mode; apply only reviewed compatible migrations.
8. Start a zero-traffic revision and verify startup/readiness.
9. Send synthetic read/write/outbox tests.
10. Shift traffic gradually; monitor errors, latency, queue/DLQ and DB load.
11. Record actual RPO/RTO and corrective actions.

Example operator outline:

```powershell
az postgres flexible-server restore `
  --resource-group rg-contoso-storefront-prod `
  --name psql-contoso-storefront-prod-restored `
  --source-server psql-contoso-storefront-prod `
  --restore-time "2026-09-03T00:30:00Z"
```

Confirm current Azure CLI syntax before an incident; this repository did not execute the command.

## Regional failover design

The current IaC is single-region. A production extension should pre-provision:

- Secondary Container Apps environment and ACR replication.
- PostgreSQL geo-restore/failover runbook.
- Replicated Key Vault strategy with independent regional vault.
- Service Bus Geo-DR alias or application-level dual namespace plan.
- Front Door health routing and WAF.
- Region-specific identities, private DNS and observability sinks.

Promote the secondary only after data consistency and dependency readiness pass. Avoid active/active writes until conflict semantics are explicitly designed.

## Exercises

Run quarterly restore tests and annual regional exercises. Record restore duration, data gap, DNS/secret propagation, migration compatibility and manual decision latency. No such Azure exercise was performed for this self-directed case study.
