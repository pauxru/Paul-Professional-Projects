# Runbook — Integrity alarm

**Alarm**: `/api/v1/verify` returned `isValid=false` for a tenant, or the `chain.verify.duration`
metric exceeded its SLO, or a scheduled verifier flagged a checkpoint mismatch.

**Severity**: **HIGH**. Integrity failures are always investigated by a human.

## Immediate actions (within 5 minutes)

1. **Do not panic-delete.** Every reaction should preserve evidence.
2. Capture the failing verification report:
   ```powershell
   curl -X POST http://<host>:5019/api/v1/verify `
     -H "Authorization: Bearer <verify-scoped token>" `
     -H "Content-Type: application/json" `
     -d '{"fromSequence":1,"toSequence":<recent seq>}'
   ```
3. Note the `brokenAtSequence`, `brokenAtEventId`, and `reason` fields.

## Triage tree

- **`reason == "contentHash mismatch"`** → payload was rewritten in place. Likely an out-of-band
  DB mutation. Preserve the DB row; check DB audit logs (Postgres pg_stat_statements) for
  recent `UPDATE`s against `AuditEvents`.
- **`reason == "chainHash mismatch"`** → chain was broken but the payload still matches. Could
  be an insert-and-relink attempt with a stale chain hash. Verify the surrounding events too.
- **`reason == "sequence gap"`** → a row was deleted (or is missing). Check DB backups and
  replication logs.
- **`reason == "previousChainHash mismatch"`** → reordering, or an event was inserted with the
  wrong previous. Look at the affected sequence's neighbours.

## Containment

1. Halt the retention pruner (`PATCH /api/v1/retention/{id}` in the future, or disable the
   scheduled job).
2. Freeze the checkpoint schedule to prevent a new signature over a compromised chain.
3. Preserve a **read-only** snapshot of the DB for forensic analysis.
4. Rotate the RSA signing key so future checkpoints are under a fresh key. Keep the old
   public key published so pre-incident signatures remain verifiable.

## Communications

- Notify the platform on-call, security team, and compliance liaison within 30 minutes.
- Draft a customer-facing message only after triage — do not speculate on root cause.

## Post-incident

- Full root-cause analysis. If the mutation happened through the ORM, the interceptor test
  suite is the first place to add a regression test.
- Consider tightening the DB role's grants further (revoke `UPDATE`/`DELETE` at the DB level
  even if the app is expected to enforce it).
- Publish a signed incident report bundled as an evidence pack, so that even the report is
  auditable.
