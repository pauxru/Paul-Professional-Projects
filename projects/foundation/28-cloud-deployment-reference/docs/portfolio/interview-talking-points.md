# Interview Talking Points

## What real problem does this solve?

It turns a runnable workload into a deployment system with explicit identity, network, migration, probe, telemetry and rollback contracts.

## Why is the architecture non-trivial?

Stable/candidate revisions share state. Schema changes must remain backward compatible, workers must survive termination, secrets must resolve before startup and promotion needs objective evidence.

## What fails at 3 a.m.?

Database/Redis/Service Bus outages, pending migrations, vault/RBAC propagation, downstream timeout storms, stuck outbox rows, bad candidate revisions, missing metrics, logical data corruption and regional loss.

## How does it recover?

Readiness removes unhealthy instances, startup wait is bounded, HTTP calls retry/break circuits, outbox records remain pending, traffic rolls back, and DR uses restore-to-new-server with progressive return.

## How is it secured?

JWT scope policies, managed identity, Key Vault references, OIDC federation, resource-scoped RBAC, rate limiting, headers and production default rejection.

## How is it tested?

66 tests include worker cancellation/checkpoints, circuit half-open, migration ordering/idempotency, probe fault semantics, API 401/403/validation and parsed Bicep/Terraform invariants.

## What trade-offs were made?

Container Apps over AKS, SQLite for local determinism, combined API/worker initially, Bicep primary plus unvalidated Terraform, and password bootstrap pending PostgreSQL Entra auth.

## What would change in an enterprise?

Custom CD role, external Entra issuer, end-to-end database identity, full private DNS/WAF/policy, image signing, Azure integration/load/DR exercises and separate worker scaling.

## Honesty statement

No Azure resources, Docker containers or Terraform commands were executed. Bicep compilation and local .NET/simulation evidence are real.
