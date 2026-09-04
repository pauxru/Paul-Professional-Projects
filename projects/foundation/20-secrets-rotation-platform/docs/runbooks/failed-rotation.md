# Runbook: Failed Rotation

## Trigger

A rotation enters `Failed` or `RolledBack`, verification reports failure, or consumer error rates
increase during a change.

## Immediate actions

1. Capture the rotation ID and correlation ID. Do not copy a secret value into the incident.
2. Query `GET /api/v1/rotations/{id}` and record state, strategy, candidate version, deadline, and
   acknowledgement statuses.
3. Confirm the prior version is still `Current`. If the candidate became current, invoke the
   explicit rollback endpoint.
4. Stop retries against the downstream target until the failure classification is understood.
5. Check `/api/v1/reports/notification-dead-letters` and consumer pull responses.

## Diagnosis by state

| State | Check |
|---|---|
| `Generating` | Generator/type registration and process logs, which must contain no value |
| `StagedNewVersion` | Encrypted row, wrapping-key version, and idempotency key |
| `NotifyingConsumers` | Channel result, URL validity, HMAC configuration, DLQ |
| `AwaitingAcknowledgement` | Missing consumer owners, deployment status, deadline |
| `Verifying` | Target availability, permissions, certificate dates, key/public-key registration |
| `Failed` | Last transition and sanitized exception classification |

## Recovery

- If the candidate is known bad, call `POST /api/v1/rotations/{id}/rollback`.
- If a transient internal error interrupted a safe state, call `POST .../{id}/resume`.
- If the old value is compromised, do not simply restore it; use the emergency revocation runbook
  and create fresh material.
- Re-run using a new idempotency key only after the previous operation is terminal.

## Exit criteria

- Exactly one current version exists.
- At most one previous version exists.
- Candidate failure is revoked, not readable.
- Consumer health is restored and the incident timeline references audit/correlation IDs.
- No plaintext was added to notes, chat, logs, or screenshots.
