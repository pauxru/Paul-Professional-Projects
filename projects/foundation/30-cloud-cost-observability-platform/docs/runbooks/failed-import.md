# Runbook: Failed or Rejected Cost Import

## Trigger
An import has rejected rows, cannot read a CSV, or a provider export is late.

## Procedure
1. Inspect `/api/v1/imports` for inserted, restated, and rejected counts.
2. Confirm CSV schema/provider (`azure` or `aws`) and required resource/date/meter columns.
3. Resolve missing inventory first. Unknown resource IDs are deliberately rejected rather than allocated to an arbitrary team.
4. Re-run the same file after inventory repair. The idempotency key updates already-seen records and inserts only missing lines.
5. Compare period total/count to provider export and retain the source file/provenance under approved data controls.

## Recovery property
Import batches can be partially committed if a process stops. Replay is safe: `(billing period, resource, meter, usage date, hour)` is unique, and `RestateFrom` replaces changed values.
