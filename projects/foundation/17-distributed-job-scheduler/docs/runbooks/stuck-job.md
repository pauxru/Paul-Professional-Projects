# Stuck Job — Claimed or Running

**System:** Project 17 — Distributed Job Scheduler & Orchestrator  
**Owner:** Northstar Platform Team  
**API:** `http://localhost:5017`

## Symptom / Alert

Use this runbook when one run remains in **Claimed** or **Running** longer than
its expected handler duration, when a cancellation appears not to take effect,
or when lease-expiry alerts increase for a specific run.

Typical signals include:

- `GET /api/v1/runs/{id}` continues to report `Claimed` or `Running`.
- The run logs stop progressing.
- `scheduler.lease.expiries` rises.
- A worker has stopped heartbeating, or its lease no longer advances.
- A handler accepted a cancellation request but continues executing.

The normal state progression is:

```text
Pending → Claimed → Running → Succeeded | Failed | Retrying | TimedOut | Cancelled | DeadLettered
```

## Severity

| Level | Use when |
| --- | --- |
| **SEV-2** | A critical workflow is blocked, multiple runs are stuck, a queue is growing, or the affected handler may have externally visible side effects. |
| **SEV-3** | One non-critical run is stuck and other work continues normally. |
| **SEV-4** | The run is confirmed to be legitimately long-running and continues to heartbeat and log progress. |

Treat a suspected duplicate side effect as SEV-2 even if only one run is
affected. Do not manually change scheduler state in SQLite.

## Preconditions / Access needed

- A valid administrator JWT in `$TOKEN`. The caller needs the applicable
  `jobs:read`, `jobs:manage`, or `jobs:admin` scope.
- Network access to `http://localhost:5017`.
- The run ID, if an alert did not already supply it.
- Read-only access to the host directory containing `jobscheduler.db` if API
  evidence is insufficient. The database uses WAL mode; use SQLite read-only
  access and never edit rows during an incident.
- Access to the worker host's approved process or deployment controls if a
  stalled worker must be stopped.
- An OpenTelemetry backend, if configured, for the named scheduler metrics.

Set a consistent PowerShell session context before starting triage:

```powershell
$BaseUri = 'http://localhost:5017'
$Headers = @{ Authorization = "Bearer $TOKEN" }
$RunId = '<run-id>'
```

## Triage

1. **Confirm the scheduler is reachable and capture the current ready state.**
   The ready response includes pending/running counts and the current leader
   owner. Preserve this output with the incident record.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/health/ready" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. **Read the exact run rather than relying only on an alert aggregate.**
   Record its state, job definition, timestamps, attempt count, lease owner,
   lease expiry, and fencing token if returned.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. **List the current population of claimed and running work.** This identifies
   whether the issue is isolated or concentrated on one worker.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Claimed" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Running" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

4. **Read the run log.** Look for the last successful handler milestone, a
   cancellation observation, an exception, a retry decision, or a long-running
   external operation.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId/logs" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

5. **Identify the owner in the worker registry.** Match the run's
   `LeaseOwner` to the node ID in this response. Inspect that node's status,
   tags, and most recent heartbeat value.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/workers" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

6. **Inspect the authoritative lease fields read-only in SQLite.** Run this
   from the directory containing `jobscheduler.db`; replace the placeholder
   with the exact run ID. This query is intentionally limited to the fields
   needed for lease diagnosis.

   ```powershell
   @'
   .parameter init
   .parameter set @runId '<run-id>'
   SELECT
       Id,
       State,
       LeaseOwner,
       LeaseExpiresAt,
       FencingToken,
       AttemptCount
   FROM JobRuns
   WHERE Id = @runId;
   '@ | sqlite3.exe -readonly .\jobscheduler.db
   ```

7. **Determine whether the lease is advancing.** Capture the run twice, at
   least one default heartbeat interval apart. The defaults are a 30-second
   lease and a 10-second heartbeat. A healthy worker should extend
   `LeaseExpiresAt`; do not equate a long execution time with a stalled lease.

   ```powershell
   $FirstObservation = Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId" `
     -Headers $Headers

   $ObservedAt = (Get-Date).ToUniversalTime().ToString('o')
   $FirstObservation | ConvertTo-Json -Depth 10

   Start-Sleep -Seconds 12

   $SecondObservation = Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId" `
     -Headers $Headers

   "First observation UTC:  $ObservedAt"
   $SecondObservation | ConvertTo-Json -Depth 10
   ```

8. **Compare the two lease expirations and worker heartbeats.**

   - A later `LeaseExpiresAt` plus a fresh worker heartbeat means the worker is
     alive. Investigate handler duration, downstream latency, or cancellation
     cooperation before disrupting it.
   - An unchanged or already expired `LeaseExpiresAt` means the owner is not
     successfully heartbeating that run.
   - A `Claimed` run may be between claim and handler start. It becomes an
     incident when its lease does not advance or it remains claimed beyond the
     expected short transition.

9. **Check that singleton maintenance has an active leader.** Reaping is a
   leader duty, so an expired lease cannot be reclaimed until a leader is
   operating.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/leader" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

10. **Record the timing before acting.** With defaults, a run whose final
    successful heartbeat occurred at time *T* normally reaches lease expiry
    around *T + 30 seconds*. The leader checks on its 5-second loop, so reclaim
    follows expiry on a subsequent healthy leader iteration. Heartbeat timing,
    not the run start time, is the relevant clock.

## Diagnosis table

| Possible cause | Signal | Confirm |
| --- | --- | --- |
| Legitimately long handler | `LeaseExpiresAt` advances; owner remains healthy; logs show progress. | Compare two observations 12 seconds apart and inspect logs. |
| Stalled worker or lost process | Lease is expired or unchanged; owner heartbeat is stale or node is dead. | Match `LeaseOwner` to `GET /api/v1/workers` and inspect the read-only lease query. |
| Temporary network loss between worker and API/database | Worker may still be running locally, but the scheduler sees no fresh lease extension. | Lease does not move; worker registry heartbeat is stale or absent; other workers may be healthy. |
| Handler ignores cancellation | Cancel request was issued, but logs/state remain `Running` while the lease continues. | Confirm a fresh lease and absence of cancellation-aware handler behavior in logs. |
| Leader is unavailable | A run has `LeaseExpiresAt <= now` but stays claimed/running beyond a leader loop. | Check `GET /api/v1/leader` and `/health/ready`; inspect the leader owner. |
| Downstream operation is slow | Worker and lease are healthy, but the handler awaits an external system. | Logs show a consistent blocking operation while heartbeats continue. |

## Remediation

1. **Preserve evidence first.** Save the run detail, logs, worker registry
   output, fencing token, and current UTC time. This is especially important
   if the handler can invoke a non-idempotent external operation.

2. **Request cooperative cancellation for work that should stop.** This is the
   supported action for an active run; it is not a force-kill.

   ```powershell
   Invoke-RestMethod -Method Post `
     -Uri "$BaseUri/api/v1/runs/$RunId/cancel" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. **Observe the result of cancellation.** A cancellation-aware handler should
   finish through the scheduler's cancellation path and the run should no
   longer remain active. Re-read both the run and its logs; do not assume the
   POST response alone means the handler has stopped.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId/logs" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

4. **For an uncooperative handler, stop the stalled worker only through the
   approved host or deployment control.** Do not edit `JobRuns`, clear a lease,
   or increment a token in SQLite. Once the worker can no longer heartbeat, the
   lease expires. The leader reaps expired `Claimed` and `Running` runs back to
   `Pending` without bumping the existing fencing token.

5. **Expect automatic reclaim after lease loss.** With the default
   `Engine:LeaseSeconds=30`, reclaim becomes eligible roughly 30 seconds after
   the final successful heartbeat, then on the next healthy
   `Engine:LeaderLoopSeconds=5` iteration. A worker that later resumes cannot
   complete using its old lease: completion is guarded by
   `WHERE FencingToken=@token AND State=Running`.

6. **Restore an active leader if there is none.** On an approved API host,
   ensure the leader role is enabled and restore the configured timing values
   appropriate for this deployment:

   ```text
   Node:RunLeader=true
   Engine:LeaderTtlSeconds=15
   Engine:LeaderLoopSeconds=5
   ```

   Apply the configuration through the deployment's normal configuration and
   restart procedure, then confirm leadership with the documented endpoint.
   Do not create or alter `LeaderLeases` directly.

7. **Allow the normal retry/DLQ policy to decide subsequent execution.** A
   reclaimed run is eligible to be claimed again. If repeated attempts exhaust
   `MaxAttempts`, it belongs in the dead-letter workflow rather than in a
   manual database repair.

## Verification

1. Re-read the run until it leaves `Claimed`/`Running` or until a clearly
   progressing, heartbeated long-running operation is documented.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. Confirm `/health/ready` reports a leader owner and that pending/running
   counts are moving in the expected direction.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/health/ready" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. Confirm the replacement claimant, if any, has a current lease and an
   appropriate fencing token. The old worker must not be able to change the
   run after it has lost ownership.

4. In the configured OpenTelemetry backend, verify that
   `scheduler.lease.expiries` stops growing unexpectedly and that
   `scheduler.claim.latency` returns to its baseline. Use the metrics backend;
   no scheduler metrics HTTP endpoint is documented.

5. Check the run log for a terminal outcome or a documented retry decision.
   If cancellation was requested, confirm the resulting state rather than
   assuming all handlers can stop immediately.

## Prevention / Follow-up

- Make handlers cancellation-aware and ensure they observe cancellation around
  long waits and external calls.
- Make externally visible work idempotent or protected by an idempotency key;
  lease recovery can result in a later attempt after a worker failure.
- Record expected maximum durations per job definition so operators can
  distinguish normal long work from a stalled execution.
- Alert separately on stale leases, dead workers, leadership loss, and growing
  queue depth; these have different responders and remediations.
- Review handler logs and dependency latency after every uncooperative
  cancellation or unexpected lease expiry.
- Do not shorten `Engine:LeaseSeconds` below realistic heartbeat and workload
  timing without testing; premature expiry increases avoidable reclaims.

## Escalation

Escalate to the Northstar Platform Team when any of the following is true:

- An expired lease remains active beyond one healthy leader loop.
- `GET /api/v1/leader` shows no sustainable leader owner.
- Multiple workers show stale heartbeats or lease expiries rise across job
  definitions.
- The run may have performed an irreversible or duplicate external side effect.
- The run repeatedly retries or becomes dead-lettered after reclaim.
- SQLite read-only inspection or API calls indicate a persistence or
  coordination failure.
