# Dead-Letter Queue Drain

**System:** Project 17 — Distributed Job Scheduler & Orchestrator  
**Owner:** Northstar Platform Team  
**API:** `http://localhost:5017`

## Symptom / Alert

Use this runbook when the dead-letter queue (DLQ) is growing, when
`scheduler.dlq.depth` is above its expected baseline, or when a job type is
repeatedly exhausting its retry attempts.

A run is dead-lettered when it exhausts `MaxAttempts` or is classified as
poison/non-retriable. The scheduler parks it in `DeadLetters`; it is not safe
to treat the DLQ as an automatic retry queue.

Typical signals are:

- `GET /api/v1/dlq` returns entries awaiting action.
- `scheduler.dlq.depth` rises or does not return to zero after an incident.
- Several entries have the same job name, reason, or error.
- A per-definition circuit opens after consecutive failures.
- Failed, retrying, or dead-lettered runs cluster around one job definition.

## Severity

| Level | Use when |
| --- | --- |
| **SEV-1** | A critical business workflow is entirely dead-lettering or the DLQ growth risks a broad production outage. |
| **SEV-2** | DLQ depth is rapidly growing, a shared dependency is failing, or several job definitions are affected. |
| **SEV-3** | A bounded set of non-critical entries needs investigation and controlled replay. |
| **SEV-4** | Historical or already-resolved entries are being reviewed with no new growth. |

## Preconditions / Access needed

- A valid administrator JWT in `$TOKEN`, with the required `jobs:read`,
  `jobs:manage`, or `jobs:admin` scope.
- Network access to the API.
- Permission to disable and later enable the affected job definition.
- The deployment's normal release/configuration controls to repair a handler,
  payload producer, or downstream dependency.
- Access to the configured OpenTelemetry backend for `scheduler.dlq.depth`.
- A written rollback decision for any replay batch. There is no documented
  bulk-replay endpoint; each replay is an intentional action.

Initialize the PowerShell session:

```powershell
$BaseUri = 'http://localhost:5017'
$Headers = @{ Authorization = "Bearer $TOKEN" }
```

## Triage

1. **Capture ready health and current workload before replaying anything.**
   The ready result includes pending/running counts and the leader owner, which
   helps distinguish an isolated handler failure from a broader scheduler
   incident.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/health/ready" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. **List the DLQ and preserve the complete response.** For every entry,
   record its ID, reason, error, attempt count, and job name as returned. If
   the response exposes an original run or job-definition ID, record that too
   for correlation.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/dlq" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. **List job definitions to map DLQ entries to their owners and schedules.**
   Use the returned definition identity and name when grouping entries. Do not
   infer a job name only from an error string.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/jobs" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

4. **Inspect the run populations for an affected definition.** Replace the
   placeholder with the verified job-definition ID. This separates an old,
   bounded DLQ from a failure that is still producing new entries.

   ```powershell
   $JobDefinitionId = '<job-definition-id>'

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Failed&jobDefinitionId=$JobDefinitionId" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Retrying&jobDefinitionId=$JobDefinitionId" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=DeadLettered&jobDefinitionId=$JobDefinitionId" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

5. **Inspect a representative original run when its ID is available from the
   DLQ response.** Read the error sequence and logs before deciding it is safe
   to replay.

   ```powershell
   $RunId = '<original-run-id>'

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId/logs" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

6. **Classify the failure before touching the queue.**

   - A repeated deterministic error for the same payload or job type is a
     poison/non-retriable candidate until the payload or handler is repaired.
   - Similar transient dependency errors across otherwise valid jobs indicate
     an outage; wait for the dependency to be demonstrably healthy before
     replay.
   - `MaxAttempts` exhaustion says retries have already been attempted; it is
     not evidence that more retries will succeed unchanged.
   - Different errors across unrelated job definitions may indicate a shared
     worker, API, database, or dependency problem rather than independent bad
     payloads.

7. **Check the per-definition circuit breaker.** The breaker opens after
   `Engine:CircuitFailureThreshold=5` consecutive failures by default and
   remains open for `Engine:CircuitCooldownSeconds=60` by default. Start with
   the job-definition response and the current run states:

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/jobs" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

   The documented endpoint list does not guarantee a named circuit-state field
   in this response and does not list a circuit-reset endpoint. If circuit
   state is not exposed, confirm it operationally from the consecutive failure
   sequence, the configured threshold/cooldown, and the absence of new
   execution attempts during the cooldown. Do not bypass it by changing data
   directly.

8. **Check whether the affected job remains enabled.** A continuing schedule
   or trigger stream can refill the DLQ faster than it can be investigated.
   If new entries are appearing, move immediately to containment.

## Diagnosis table

| Possible cause | Signal | Confirm |
| --- | --- | --- |
| Poison payload or non-retriable handler error | The same definition and deterministic reason/error recur; entries may be classified poison before normal retries. | Compare representative DLQ entry details, the original run log, and the payload-producing workflow. |
| Transient downstream outage | Multiple otherwise valid jobs show a common dependency error over the same period. | Validate dependency recovery through its approved operational checks before replaying even one entry. |
| `MaxAttempts` exhausted | Attempt count reaches the definition's retry limit before the entry is parked. | Inspect the entry's attempt count, run history/logs, and the definition's retry policy. |
| Circuit breaker open | Consecutive failures reach the threshold; the definition stops consuming fleet capacity during cooldown. | Check the definition/current runs and configured `Engine:CircuitFailureThreshold` and `Engine:CircuitCooldownSeconds`. |
| Ongoing schedule or trigger source | DLQ depth grows after prior entries are reviewed. | Compare repeated list results and inspect the affected definition; disable it to stop additional scheduled work. |
| Missing eligible worker capacity | A job cannot make normal progress because required tags have no healthy worker. | Inspect `GET /api/v1/workers` and compare tags with the affected definition's requirements. |

## Remediation

1. **Contain an actively failing definition before repairing or replaying it.**
   Disable the verified offending definition. This stops new scheduled work for
   that definition while preserving the DLQ evidence.

   ```powershell
   Invoke-RestMethod -Method Post `
     -Uri "$BaseUri/api/v1/jobs/$JobDefinitionId/disable" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. **Repair the root cause outside the DLQ.**

   - For a poison job, correct the payload producer or handler and deploy it
     through the normal release process.
   - For a dependency outage, restore and validate the dependency first.
   - For missing worker eligibility, restore healthy workers with the required
     tags before replay.
   - Do not edit `DeadLetters`, `JobRuns`, attempts, or circuit state in
     SQLite.

3. **Respect the circuit breaker.** It exists so one failing definition cannot
   consume the fleet. Do not use a replay batch to test a dependency that is
   still broken. If configuration review is necessary, preserve the intended
   protection:

   ```text
   Engine:CircuitFailureThreshold=5
   Engine:CircuitCooldownSeconds=60
   ```

   Apply any approved configuration change through the normal deployment
   process; no API operation to force-open, force-close, or reset a circuit is
   documented.

4. **Prove the repair with a controlled new trigger when it is safe to do so.**
   Keep the definition disabled until the repair is ready, then enable it and
   create one deliberate test run. Monitor that run to a terminal result before
   replaying parked production work.

   ```powershell
   Invoke-RestMethod -Method Post `
     -Uri "$BaseUri/api/v1/jobs/$JobDefinitionId/enable" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Post `
     -Uri "$BaseUri/api/v1/jobs/$JobDefinitionId/trigger" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

5. **Replay one verified DLQ entry.** Replay creates a fresh `Pending`
   instance of the original run, resets its `AttemptCount` to `0`, and marks
   the dead-letter entry replayed. It does not make a previously broken
   dependency healthy.

   ```powershell
   $DlqId = '<verified-dlq-entry-id>'

   Invoke-RestMethod -Method Post `
     -Uri "$BaseUri/api/v1/dlq/$DlqId/replay" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

6. **Observe the replay before issuing the next one.** Re-list the DLQ,
   inspect pending/running work for the definition, and use the returned IDs
   where available to follow the replayed run. Stop immediately if the same
   reason or error returns.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/dlq" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Pending&jobDefinitionId=$JobDefinitionId" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

7. **Drain in small, manually approved batches only after the controlled replay
   succeeds.** There is no documented bulk-replay endpoint. Use a curated list
   of verified IDs, keep batch sizes within available worker/dependency
   capacity, and pause between batches to inspect results.

   ```powershell
   $DlqIds = @(
     '<verified-dlq-entry-id-1>',
     '<verified-dlq-entry-id-2>'
   )

   foreach ($EntryId in $DlqIds) {
     Invoke-RestMethod -Method Post `
       -Uri "$BaseUri/api/v1/dlq/$EntryId/replay" `
       -Headers $Headers | ConvertTo-Json -Depth 10

     Start-Sleep -Seconds 2
   }
   ```

8. **Re-disable the definition if replay reveals that the cause persists.**
   This contains new work while the incident is reclassified and prevents a
   replay loop from creating more failed attempts.

   ```powershell
   Invoke-RestMethod -Method Post `
     -Uri "$BaseUri/api/v1/jobs/$JobDefinitionId/disable" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

## Verification

1. Re-list the DLQ after every batch. Confirm that no entries remain awaiting
   action; replayed entries should be marked replayed as applicable.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/dlq" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. Verify the replayed work progresses from fresh `Pending` execution to a
   successful terminal outcome rather than returning to `Retrying` or
   `DeadLettered`.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=DeadLettered&jobDefinitionId=$JobDefinitionId" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. Confirm `/health/ready` shows stable pending/running counts and an active
   leader owner.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/health/ready" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

4. In the configured OpenTelemetry backend, verify
   `scheduler.dlq.depth` returns to zero for active, unreplayed entries and
   remains there after the job definition is re-enabled. Also check that
   `scheduler.queue.depth` and `scheduler.run.duration` remain within normal
   operating range.

## Prevention / Follow-up

- Validate payloads as early as possible so poison work is rejected before it
  repeatedly consumes scheduler attempts.
- Make retry classification explicit: retain retries for genuinely transient
  failures and classify non-retriable errors as poison.
- Use the circuit breaker as a fleet-protection mechanism, not as an alert
  suppression mechanism.
- Alert on DLQ growth by job definition, reason, and error pattern—not only on
  a global depth total.
- Exercise one-entry replay in operational testing so responders know the
  expected handler behavior and external side effects.
- Document dependency readiness checks and idempotency expectations for each
  high-volume job definition.
- Keep scheduled definitions disabled during repairs until a controlled trigger
  succeeds.

## Escalation

Escalate to the Northstar Platform Team when:

- DLQ depth continues increasing after the suspected definition is disabled.
- A controlled replay fails with the same reason after the stated root cause
  was repaired.
- Multiple unrelated definitions dead-letter together.
- The circuit appears not to contain repeated failures or cannot recover after
  the configured cooldown.
- Replayed work may create duplicate or irreversible external effects.
- API, worker, leadership, SQLite, or downstream-dependency symptoms prevent
  safe classification of the entries.
