# ADR-005 — Resolve secret references at execution time

## Context

Connector and flow definitions must be portable and reviewable without embedding credentials. Rotation must not require rewriting every definition.

## Options

1. Store literal credentials in connector configuration.
2. Inject all values from environment variables by convention.
3. Store names such as `@secret:crm/apiKey` and resolve the current version at call time.

## Decision

Choose option 3 behind `ISecretStore`. The local adapter stores versioned values in an AES-256-GCM envelope using a master key supplied by configuration or `INTEGRATIONHUB_SECRETS_MASTERKEY`.

## Consequences

Rotation is atomic from the connector's perspective and definitions contain no value. The redactor learns resolved values and removes them from snapshots and errors.

## Risks

The local key is still present in process memory, and a filesystem adapter cannot provide enterprise access controls or hardware-backed keys.

## Alternatives

Production should implement the same port with Azure Key Vault, AWS Secrets Manager, or HashiCorp Vault. Environment-only injection is simple but weak for inventory and rotation workflows.
