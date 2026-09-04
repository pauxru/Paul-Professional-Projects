# Worker Node Down or Unresponsive

**System:** Project 17 — Distributed Job Scheduler & Orchestrator  
**Owner:** Northstar Platform Team  
**API:** `http://localhost:5017`

## Symptom / Alert

Use this runbook when a worker has crashed, stopped registering heartbeats,
become unreachable, or appears as a dead node while it owns active work.

Common triggers are:

- `GET /api/v1/workers` shows an old heartbeat or a node marked `Dead`.
- Runs owned by one node remain `Claimed` or `Running` until their leases
  expire.
- `scheduler.lease.expiries` increases after a worker outage.
- Required worker tags are no longer represented by healthy nodes.
- `/health/ready` reports growing pending or running counts during a worker
  loss.

The default timing matters during diagnosis:

| Setting | Default | Operational meaning |
| --- | ---: | --- |
| `Engine:LeaseSeconds` | 30 seconds | A run can be reclaimed after its lease expires if heartbeats stop. |
| `Engine:HeartbeatSeconds` | 10 seconds | A healthy worker ordinarily refreshes its run lease at this cadence. |
| `Engine:NodeTtlSeconds` | 45 seconds | A node past this heartbeat age is marked `Dead`; its in-flight work is reclaimed. |
| `Engine:LeaderLoopSeconds` | 5 seconds | The leader's recurring maintenance loop, including lease reaping. |

## Severity

| Level | Use when |
| --- | --- |
| **SEV-1** | All eligible workers for a critical required tag are down, or the outage stops a critical business workflow. |
| **SEV-2** | Multiple workers are unavailable, pending work is growing, or active runs are failing to recover. |
| **SEV-3** | One worker is unavailable and healthy capacity remains. |
| **SEV-4** | A planned, healthy drain is visible and all in-flight work is completing. |

## Preconditions / Access needed

- A valid administrator JWT in `$TOKEN`, with access appropriate to
  `jobs:read`, `jobs:manage`, or `jobs:admin`.
- Network access to the scheduler API.
- The node ID from the alert or from the worker registry.
- Permission to use the approved worker-host or deployment restart controls.
- The intended worker tags for the node. Required tags determine which jobs the
  node is eligible to claim.
- Optional read-only access to the directory containing `jobscheduler.db`.
  Do not make emergency state changes in SQLite.

Initialize the diagnostic session:

```powershell
$BaseUri = 'http://localhost:5017'
$Headers = @{ Authorization = "Bearer $TOKEN" }
$NodeId = 'w1'
```

## Triage

1. **Capture scheduler readiness before changing capacity.** The ready result
   includes pending/running counts and the leader owner, which distinguishes a
   worker problem from a broader coordination problem.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/health/ready" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. **Inspect the worker registry.** Locate `$NodeId`, then record its
   `WorkerStatus`, tags, and most recent heartbeat shown by the response.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/workers" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. **Calculate the observed heartbeat age using a common UTC reference.**
   Compare the registry's last-heartbeat timestamp to the following time. A
   heartbeat older than the configured `Engine:NodeTtlSeconds` (45 seconds by
   default) is eligible to be treated as a dead node.

   ```powershell
   (Get-Date).ToUniversalTime().ToString('o')
   ```

4. **Find work currently owned by the node.** List both active states because a
   crashed process can be interrupted just after claim or during execution.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Claimed" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Running" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

5. **Read the owned active runs directly from SQLite when a precise ownership
   view is needed.** Run this from the directory containing `jobscheduler.db`.
   It is read-only and returns only the lease evidence required for recovery.

   ```powershell
   @'
   .parameter init
   .parameter set @nodeId 'w1'
   SELECT
       Id,
       State,
       LeaseOwner,
       LeaseExpiresAt,
       FencingToken,
       AttemptCount
   FROM JobRuns
   WHERE LeaseOwner = @nodeId
     AND State IN ('Claimed', 'Running')
   ORDER BY LeaseExpiresAt;
   '@ | sqlite3.exe -readonly .\jobscheduler.db
   ```

6. **Inspect one representative run and its logs.** This establishes whether
   the handler was making progress before the node disappeared.

   ```powershell
   $RunId = '<owned-active-run-id>'

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/$RunId/logs" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

7. **Check that a leader is active.** Lease reaping and retention are leader
   duties. A lost worker can leave expired runs visible longer if there is no
   functioning leader loop.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/leader" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

8. **Separate a graceful drain from a crash.**

   - A node in `WorkerStatus.Draining` intentionally stops claiming new runs
     and finishes its in-flight work. Its active leases should continue to be
     managed until that work completes.
   - A crashed or partitioned node stops making usable heartbeats. Its leases
     cease advancing, then expire; the node is marked `Dead` after its node
     TTL and its work is reclaimed.
   - Do not restart or terminate a healthy draining node merely because it is
     no longer claiming new work.

9. **Observe the two recovery clocks.** An in-flight run becomes eligible for
   leader reaping when `LeaseExpiresAt <= now`—normally about 30 seconds after
   its final successful heartbeat under default settings. Independently, a
   node older than the 45-second node TTL is marked dead and its in-flight work
   is reclaimed. Record both times rather than waiting for a fixed delay from
   the original run start.

## Diagnosis table

| Possible cause | Signal | Confirm |
| --- | --- | --- |
| Worker process crashed | Node heartbeat stops; leases stop advancing; node later becomes `Dead`. | Check the worker registry, active run leases, and approved host process evidence. |
| Network or API/database connectivity loss | Worker may still exist locally, but its scheduler heartbeat or run lease is stale. | Compare local host evidence, registry heartbeat, and `LeaseExpiresAt`; confirm other nodes can use the API. |
| Planned drain | `WorkerStatus.Draining`; no newly claimed work; existing work finishes. | Confirm the approved drain operation and watch each active run reach a terminal state. |
| Required-tag capacity loss | Pending jobs accumulate while no healthy worker has the required tags. | Compare worker tags in the registry with the affected job definitions' required tags. |
| No active leader | Leases are expired but runs do not return to `Pending`. | Check `GET /api/v1/leader` and `/health/ready`. |
| Broad scheduler outage | Several nodes have stale heartbeats or API health is degraded. | Check all workers, ready health, leader ownership, and the affected host/network domain. |

## Remediation

1. **Do not manually release leases or edit worker state in the database.**
   Recovery is intentionally coordinated through leases, reaping, and fencing.
   Manual SQLite edits can bypass the evidence needed to prevent stale writes.

2. **For a confirmed crash, allow automatic recovery to occur.** After the
   final heartbeat, the active run lease expires according to
   `Engine:LeaseSeconds` (30 seconds by default). A functioning leader reaps
   expired `Claimed`/`Running` runs to `Pending`; another eligible worker can
   then claim them. The node TTL also marks a non-heartbeating node `Dead` and
   reclaims its in-flight work.

3. **Restore a leader if automatic reclaim is not occurring.** On an approved
   API host, restore the leader setting through the normal configuration and
   restart procedure:

   ```text
   Node:RunLeader=true
   Engine:LeaderTtlSeconds=15
   Engine:LeaderLoopSeconds=5
   ```

   Confirm the resulting owner rather than modifying the `LeaderLeases` row.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/leader" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

4. **Restart a standalone worker with its correct identity and tags.** From the
   Project 17 repository root, use the documented worker command. Keep the
   node ID and tags aligned with the intended worker pool.

   ```powershell
   Set-Location 'C:\Users\rukwaropaul\Downloads\DEV\Projects\17-distributed-job-scheduler'
   dotnet run --project src/JobScheduler.Worker -- --node-id w1 --tags etl,reports
   ```

   Do not leave two independently running processes using the same intended
   node identity. First establish whether the original process has stopped or
   been isolated through the approved host controls.

5. **Restore in-process worker capacity when that is the intended topology.**
   An API host can run an in-process worker and leader. Apply this setting via
   the deployment's normal configuration process and restart that host:

   ```text
   Node:RunWorker=true
   Node:RunLeader=true
   ```

   Use this only for the API-host topology; use the standalone command above
   for dedicated worker hosts.

6. **For a planned maintenance event, drain rather than crash.** Use the
   environment's approved host/deployment operation that transitions the node
   to `WorkerStatus.Draining`, wait for its in-flight runs to finish, then stop
   it. No worker-drain API endpoint is listed for this scheduler, so do not
   invent one or substitute a database update.

7. **Restore tag coverage before enabling new throughput.** If the failed node
   was the only node eligible for `etl`, `reports`, or another required tag,
   start replacement capacity with matching tags before expecting those jobs to
   drain.

## Verification

1. Confirm the replacement or restarted node registers and resumes fresh
   heartbeats.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/workers" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. Re-read the runs formerly owned by the unavailable node. Each should either
   finish, retry according to policy, be reclaimed and claimed by an eligible
   worker, or enter the documented dead-letter flow after exhausted attempts.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Pending" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Running" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. Inspect a recovered run by ID and confirm that its current owner and state
   are current. If a new worker claimed it, its fencing token was minted
   monotonically for that claim.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs/<recovered-run-id>" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

4. Confirm a revived former worker cannot corrupt recovered state. Its late
   completion is rejected because completion requires both its original
   `FencingToken` and `State=Running`. After reaping, state is `Pending`; after
   a replacement claim, the token is newer. In either case, the zombie write
   cannot satisfy the guarded completion update.

5. In the configured OpenTelemetry backend, verify that
   `scheduler.lease.expiries` stabilizes and that `scheduler.runs.claimed`
   resumes on healthy capacity. Confirm `/health/ready` reports expected
   pending/running counts and an active leader owner.

## Prevention / Follow-up

- Maintain at least one healthy worker for every required tag, with additional
  capacity for critical job types.
- Use graceful draining for planned maintenance; reserve crashes/restarts for
  failed nodes.
- Alert before the node TTL is crossed so an operator can distinguish transient
  heartbeat delay from confirmed node loss.
- Monitor `scheduler.lease.expiries`, `scheduler.runs.claimed`, queue depth,
  worker heartbeats, and tag coverage together.
- Document the intended node IDs, tags, and whether each pool is standalone or
  in-process on an API host.
- Review worker process, network, and dependency failures after every repeated
  dead-node event.

## Escalation

Escalate to the Northstar Platform Team when:

- No healthy worker remains for a required tag.
- Active runs are still not reclaimed after lease expiry with an active leader.
- More than one worker enters a dead or stale-heartbeat condition.
- The restarted worker cannot register or heartbeat successfully.
- A revived node appears able to affect recovered work, indicating a fencing
  or persistence concern.
- The incident involves a broader API, SQLite, host, or network outage.
