# Runbook — Contract Drift

## Trigger

An alert reports a `new`, `missing`, or `retyped` field for a connector operation.

## Assessment

1. Query `GET /api/v1/runs/contract-drift?unresolvedOnly=true`.
2. Identify affected connector version, operation, field path, and first-seen time.
3. Compare the descriptor contract with the vendor changelog or simulator response.
4. Determine impact:
   - New optional field: normally low risk, but review if additional fields are denied.
   - Missing required field: stop dependent flows.
   - Retyped field: stop dependent flows unless coercion is explicitly safe.
5. Review recent run snapshots; they are redacted, so use a secure vendor trace if value-level evidence is required.

## Remediation

1. Create a new connector version or flow version; do not mutate an active historical definition.
2. Add/update contract and mapping tests.
3. Test in the mapping bench and against a simulator fixture representing the new response.
4. Activate the new flow version.
5. Replay affected DLQ items with their original idempotency keys.
6. Resolve/archive the alert through the operational data process.

## Escalation

Escalate immediately when a missing/retyped field affects identity, amount, currency, settlement status, or target routing.
