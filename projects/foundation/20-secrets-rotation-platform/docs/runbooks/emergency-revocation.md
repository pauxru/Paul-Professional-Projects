# Runbook: Emergency Revocation

## Trigger

Use when compromise is suspected, a consumer is breached, a signing key is exposed, or normal
graceful rotation is unsafe.

## Safety

Emergency revocation invalidates all versions immediately and can cause an outage. It requires an
expiring four-eyes approval. Never paste the compromised value into the request reason.

## Procedure

1. Authenticate with `secrets.breakglass`; identify the exact secret path or application prefix.
2. Create an `EmergencyRevoke` or `IncidentRotation` approval request with an incident reference.
3. A different authorized actor authenticates with `secrets.approve` and approves the request.
4. The requester executes `/api/v1/break-glass/revoke`, or executes
   `/api/v1/break-glass/incident` for the application prefix.
5. Confirm every affected version is `Revoked`.
6. For incident mode, monitor each generated rotation to completion and consumer acknowledgement.
7. In the real target system, revoke provider-side credentials and validate denial.
8. Search the audit report by correlation ID and export evidence to the incident record.

## Containment checks

- Rotate the wrapping key if database ciphertext and the master key may both be exposed.
- Rotate webhook signing material if notice authenticity is in doubt.
- Invalidate JWT signing keys and workload sessions if control-plane identity is compromised.
- Inspect unusual readers, first-time readers, spikes, and out-of-hours access.

## Exit criteria

Compromised versions are unusable, replacements are verified, consumers acknowledge, the old
provider-side credentials are revoked, and a post-incident review owns every follow-up.
