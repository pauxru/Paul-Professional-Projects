# ADR-006 — Use GitHub OIDC Federation for Azure Deployment

## Context

GitHub Actions needs Azure control-plane access without a long-lived service-principal password or certificate.

## Options

1. Client secret stored in GitHub.
2. Certificate credential stored in GitHub.
3. Workload identity federation using GitHub OIDC.

## Decision

Create an Entra application/service principal with federated credentials scoped to each GitHub Environment subject. Store only non-secret client, tenant and subscription IDs as environment variables. Grant deployment roles at the narrowest practical resource-group scope.

## Consequences

- Azure login receives a short-lived token per job.
- There is no Azure credential secret to rotate in GitHub.
- GitHub Environment approvals protect staging and production.
- Repository/environment/branch subject strings become security-sensitive configuration.

## Risks

- A broad subject or RBAC scope can permit unintended workflows.
- Renaming a repository or environment breaks federation.
- Compromised workflow code can use the job’s authorized scope.

## Alternatives

Managed runners with Azure managed identity can be stronger in some enterprises. Client secrets are supported by tooling but are rejected here because their lifecycle and leakage risk are unnecessary.
