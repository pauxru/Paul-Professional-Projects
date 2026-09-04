# ADR-005 — Authoritative-source provisioning reconciliation

## Context

Successful API calls do not prove a target still matches desired state. Accounts may be created outside IGA and entitlements may be granted directly.

## Options

1. Trust forward provisioning only.
2. Periodically overwrite every target.
3. Compare independent target snapshots with authoritative identities and effective IGA access.

## Decision

Define `IProvisioningConnector` with create, update, disable, delete, and account listing. Persist forward jobs, attempts, failures, and quarantine. Reconciliation independently compares target snapshots:

- no active source identity → orphan account;
- target permission absent from the user's effective IGA derivations for that application → rogue grant.

## Consequences

- Drift is visible even when forward provisioning reported success.
- Simulated targets can demonstrate target-side bypass without infrastructure.
- Findings feed orphan and rogue-grant reports and audit.

## Risks

- Target APIs can be eventually consistent.
- Permission-name mapping must be normalized per connector.
- Full scans can be costly and need incremental cursors at scale.

## Alternatives

Forward-only trust was rejected because it misses out-of-band changes. Blind overwrite was rejected because it can destroy legitimate emergency state before review and produces poor evidence.
