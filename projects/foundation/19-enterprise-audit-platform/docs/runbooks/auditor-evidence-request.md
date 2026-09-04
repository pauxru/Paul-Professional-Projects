# Runbook — Auditor evidence request

**Purpose**: satisfy an auditor's request for evidence of specific actions in a specific
time window with an artefact they can independently verify.

## Precondition: know exactly what is being asked for

- Tenant id, time range, and (optionally) filter by resource, actor, or category.
- The requesting auditor's identity — record it in your ticketing system before you start.

## Step 1 — Build the evidence pack

Under `audit:export` scope:

```powershell
curl -X POST http://<host>:5019/api/v1/exports/evidence `
  -H "Authorization: Bearer <export token>" `
  -H "Content-Type: application/json" `
  -d '{ "from": "2026-01-01T00:00:00Z", "to": "2026-06-30T23:59:59Z" }' `
  -o evidence-pack.json
```

The pack contains:

- **Events** in the requested time range for the caller's tenant.
- **Checkpoints** covering those events, each with its `MerkleRoot` and RSA signature.
- **Manifest** with event count, checkpoint count, and `bundleHash`.
- **Signature** over the bundle hash and the `SigningKeyId`.

## Step 2 — Independently verify (recommended before handover)

Round-trip through the platform to make sure the pack is well-formed:

```powershell
curl -X POST http://<host>:5019/api/v1/exports/evidence/verify `
  -H "Authorization: Bearer <verify token>" `
  -H "Content-Type: application/json" `
  --data-binary "@evidence-pack.json"
```

Expected response: `{"valid":true}`.

Also run a chain verification to be sure the underlying tenant's chain is clean:

```powershell
curl -X POST http://<host>:5019/api/v1/verify `
  -H "Authorization: Bearer <verify token>" `
  -H "Content-Type: application/json" `
  -d '{"fromSequence": 1, "toSequence": 999999999}'
```

If either of these fails, **do not** hand over the pack. Escalate under `integrity-alarm.md`.

## Step 3 — Deliver

- Bundle: `evidence-pack.json`, the RSA public key(s) referenced by `SigningKeyId`, and the
  copy of `docs/integrity-model.md` so the auditor knows how to re-verify without your API.
- Transport: encrypt in transit; expect a secure file-transfer channel from the auditor.
- Retain your own copy of the pack in the immutable-archive location; **do not delete** the
  local copy at least until the audit is complete.

## Step 4 — Record the request

Emit an audit event of type `audit.evidence.exported` with the auditor's identity, the time
range, and the pack's `bundleHash` (already recorded automatically if the export goes through
the API — verify).

## What the auditor sees

- A tenant-scoped, time-bounded list of events with cryptographic evidence.
- Checkpoint signatures the auditor can verify against the published public keys.
- No cross-tenant leakage — impossible by construction (see ADR-005).
