# Portfolio Summary

## Classification

Self-directed engineering case study; Azure deployment reference architecture.

## Problem

Show how a modest .NET workload should be packaged, configured, migrated, observed, released and rolled back on Azure without pretending that an architecture diagram is a deployment.

## Built

- Runnable .NET 10 catalogue/order API and checkpointed outbox worker.
- Separate EF migration executable with SQLite/PostgreSQL/SQL Server switch.
- Proper startup/liveness/readiness, graceful drain and dependency wait.
- OpenTelemetry, correlation, resilient outbound client and dark-launch flag.
- Container Apps-targeted Bicep plus Terraform equivalent.
- OIDC GitHub Actions, migration ordering and progressive release scripts.
- 66 behavioural/IaC tests and operator documentation.

## Verification

Release build and 66 tests passed. Bicep main and three parameter files compiled with Azure CLI/Bicep. Healthy and failing local canary simulations exercised promote/rollback. Terraform, Docker and Azure deployment were not executed.

## Senior engineering signal

The project concentrates on failure handling, migration compatibility, identity boundaries, rollout evidence and honest gaps—areas that determine whether cloud delivery is safe.
