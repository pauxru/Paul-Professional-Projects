# ADR-002 — Use Managed Identity and Key Vault References

## Context

Container credentials, database passwords, cache keys and telemetry connection strings must not be committed or stored as literal application settings.

## Options

1. Plain environment variables populated by a pipeline.
2. Container Apps secrets containing literal values.
3. User-assigned managed identity with Key Vault references.

## Decision

Use a user-assigned managed identity. Grant resource-scoped Key Vault Secrets User, AcrPull, and Service Bus Data Sender/Receiver roles. Store required bootstrap values in Key Vault and configure Container Apps secrets with `keyVaultUrl` plus `identity`.

## Consequences

- No Azure client secret is stored in GitHub.
- Application settings contain secret references, not secret values.
- Identity lifecycle and role assignments are explicit IaC.
- The same identity is available to the Key Vault configuration adapter and Service Bus SDK.

## Risks

- RBAC propagation can delay first deployment.
- Over-broad role scope would defeat the design; tests and review must preserve resource scope.
- PostgreSQL still uses a bootstrap password in this reference and should move to Entra token authentication.

## Alternatives

Pipeline-injected values are simpler but increase secret exposure and rotation coupling. System-assigned identity reduces one resource but makes stable cross-revision role assignment and reuse by jobs less explicit.
