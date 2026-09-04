# ADR-005 — Provide Bicep as Primary IaC and Terraform as an Equivalent

## Context

Azure-native teams often prefer Bicep, while consulting clients may standardize on Terraform and remote state.

## Options

1. Bicep only.
2. Terraform only.
3. Maintain equivalent Bicep and Terraform references.

## Decision

Treat Bicep as the primary, syntax-checked Azure reference and include a structurally equivalent Terraform implementation with modules, environment variables and an Azure Storage backend example.

## Consequences

- Azure resource semantics are directly visible in Bicep.
- Teams can evaluate either operational model.
- IaC invariant tests compare environment intent independent of tool.
- Two implementations increase maintenance cost and drift risk.

## Risks

- Terraform was not executable on the build host and may contain provider-schema issues.
- Resource parity can drift.
- Neither implementation was applied to Azure.

## Alternatives

Choose one tool in a real engagement after considering organizational skills, policy tooling, state ownership and existing module ecosystems. Do not maintain two indefinitely without automated parity controls.
