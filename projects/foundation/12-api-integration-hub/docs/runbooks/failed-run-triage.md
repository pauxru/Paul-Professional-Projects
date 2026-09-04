# Runbook — Failed Run Triage

## Trigger

A run is `Failed` or `PartiallySucceeded`, or the failure counter increases.

## Procedure

1. Capture the run id and `X-Correlation-Id`.
2. Query `GET /api/v1/runs/{runId}` with `hub.read`.
3. Find the first failed step in the timeline; compare `recordsIn` and `recordsOut`.
4. Check `/api/v1/runs/contract-drift?unresolvedOnly=true`.
5. Check `/api/v1/dead-letters?status=Pending`.
6. Classify:
   - `429`: verify `Retry-After`, connector retry count, and vendor quota.
   - `5xx`/timeout: inspect circuit state and target health.
   - `401`: rotate credential or verify OAuth client configuration.
   - `412`: resolve ETag/concurrency conflict.
   - `422`: inspect the quarantined redacted payload and target contract.
   - drift alert: pause the flow if mapping safety is uncertain.
7. Correct the target, secret, mapping, or connector configuration.
8. Replay the narrowest safe scope: one record before a batch, a batch before a whole run.
9. Verify replay status and target-side idempotency response.
10. Record the cause, remediation, and whether a contract or alert rule must change.

## Do not

- Do not edit a DLQ payload in the database.
- Do not generate a new idempotency key for an ambiguous prior write.
- Do not paste raw credentials or unredacted payloads into tickets.
