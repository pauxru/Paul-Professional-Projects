# Runbook — Integrity Verification

**When to use**: routine periodic integrity checks, after any incident, after a restore from backup,
or whenever a trial-balance or reconciliation alert fires. This is the authoritative "are the books
sound?" procedure.

## What the check covers

`GET /api/v1/admin/integrity/verify` (scope `ledger:admin`) returns an `IntegrityReport` with two
independent parts:

1. **Hash-chain verification** (`chain`): walks every entry in sequence order, recomputes
   `SHA-256(previousHash ‖ canonicalContent)`, and confirms each link. Reports `isValid`,
   `entriesChecked`, and `firstBrokenSequence` (null if intact).
2. **Balance reconciliation** (`reconciliation`): for every account, compares the **cached**
   debit/credit totals against the totals **derived** by summing postings. Reports `isReconciled`,
   `accountsChecked`, and a list of `discrepancies`.

`isHealthy = chain.isValid && reconciliation.isReconciled`.

## Procedure

```powershell
$report = Invoke-RestMethod "$base/api/v1/admin/integrity/verify" -Headers $adminAuth
$report | ConvertTo-Json -Depth 6
```

Interpret:

- `isHealthy = true` → the ledger is internally consistent and untampered. Record the result and the
  timestamp. Done.
- `isHealthy = false` → escalate to **Sev-1** and continue below.

## If the hash chain is broken (`chain.isValid = false`)

`firstBrokenSequence` is the earliest entry whose recomputed hash does not match. Everything from that
sequence onward is suspect.

1. **Do not write to the ledger.** It is append-only; do not attempt manual edits.
2. **Preserve evidence.** Snapshot the database file and the logs immediately.
3. **Determine the cause.** A broken chain means an entry (or its ordering) changed out-of-band —
   i.e. someone/something wrote to the database directly, or a backup/restore was inconsistent. This
   is a security event, not a normal application path (the app cannot mutate sealed entries).
4. **Recover from a known-good backup**: restore the most recent backup whose
   `integrity/verify` returns `isHealthy = true`, then re-apply legitimate activity after the last
   good sequence from an independent source-of-truth (e.g. upstream payment records / correlation
   ids), re-posting through the API so new entries re-seal correctly.
5. **Root-cause the write path** that allowed out-of-band mutation before returning to service.

## If balances don't reconcile (`reconciliation.isReconciled = false`)

The hash chain being valid but balances not reconciling means the **cached** running totals drifted
from the postings (the postings — the source of truth — are intact).

1. Inspect `reconciliation.discrepancies`: each entry names the account and its cached vs derived
   debit/credit totals.
2. The **derived** totals (sum of postings) are authoritative. The cached totals are a performance
   optimisation.
3. Correct by **re-deriving** the cached balances from the postings for the affected accounts (a
   maintenance operation), never by editing history. Then re-run the verification.
4. Root-cause how the cache drifted (this should not happen — the concurrency protocol maintains cache
   and postings in the same transaction). Capture the correlation ids around the affected entries.

## Routine cadence & alerting

- Run `integrity/verify` on a schedule (e.g. hourly) and export `isHealthy` as a gauge.
- Page immediately on `isHealthy = false`.
- Also alert on trial-balance `isBalanced = false` (a cheaper, related signal).
- Keep verified-healthy backups so recovery has a known-good restore point.

## Related

- `unbalanced-entry-alert.md` — rejected postings and non-zero trial balance.
- `docs/decisions/ADR-004-hash-chain-tamper-evidence.md` — how and why the chain works, and its limits.
- `docs/concurrency-notes.md` — why cached balances should always reconcile.
