# Runbook — Incident Scale-Out

## Trigger

Sustained latency/CPU/concurrency or queue-depth pressure with healthy dependencies.

## Diagnose first

1. Confirm demand rather than retry storm or dependency failure.
2. Check Container Apps replica count and throttling.
3. Check PostgreSQL CPU/connections/locks/IOPS.
4. Check Redis memory/evictions.
5. Check Service Bus active/DLQ count and message age.
6. Check candidate/stable revision split.

Scaling the API during a database bottleneck can worsen the incident.

## Temporary scale action

```powershell
az containerapp update `
  -g RESOURCE_GROUP `
  -n APP_NAME `
  --min-replicas 6 `
  --max-replicas 30
```

If worker backlog is the issue and API traffic is normal, prefer splitting the worker into its own Container App before making permanent changes. The current combined process scales both.

## Guardrails

- Confirm DB connection headroom before adding replicas.
- Preserve at least two replicas during a production incident.
- Watch cost and log ingestion.
- Do not bypass readiness.
- Roll back a bad candidate before scaling it.

## Recovery

When metrics remain stable through two observation windows, return IaC to the approved replica range and deploy the configuration through review. Document whether the permanent fix is capacity, query optimization, backpressure or workload separation.
