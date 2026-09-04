# ADR-005: Enforce Error-Budget Policy Through a Deploy Gate API

## Context
A written policy such as “freeze risky changes below 25% budget” is easy to overlook during delivery pressure. CI/CD systems need a machine-readable decision.

## Options
1. Display budget status only.
2. Send a human-readable warning.
3. Publish a scoped deploy-gate endpoint with configurable thresholds and reason text.

## Decision
Choose option 3. `GET /api/v1/gates/{service}/deploy` returns allow/deny, policy action, reason, and remaining percentage. Defaults are warn below 50%, deny risky deploys below 25%, and deny all changes at exhaustion.

## Consequences
Automation can enforce the same policy displayed to humans. The API remains advisory unless a delivery pipeline consumes it; that integration is intentionally not claimed.

## Risks
An SLO can be mis-scoped, causing an inappropriate gate. Policies need human review, override governance, and an auditable emergency path in production.

## Alternatives
A direct CI plugin is possible, but an HTTP boundary is simpler to integrate from heterogeneous delivery systems and easier to test independently.
