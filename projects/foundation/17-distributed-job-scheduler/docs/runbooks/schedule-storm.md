# Schedule Storm or Thundering Herd

**System:** Project 17 — Distributed Job Scheduler & Orchestrator  
**Owner:** Northstar Platform Team  
**API:** `http://localhost:5017`

## Symptom / Alert

Use this runbook when due work arrives faster than the eligible fleet can
claim it: a misfire catch-up after downtime, an interval scheduled too tightly,
or a flood of external trigger requests.

Typical alerts and symptoms include:

- `scheduler.queue.depth` rises sharply or remains elevated.
- `GET /api/v1/runs?state=Pending` returns an unexpectedly large backlog.
- `scheduler.claim.latency` increases while workers are healthy.
- `/health/ready` reports rising pending counts.
- One recurring job definition dominates pending work.
- A burst follows API/leader downtime or a schedule-definition change.
- External callers are invoking `POST /api/v1/jobs/{id}/trigger` faster than
the intended operating rate.

This runbook is for excess **arrival rate**, not only failed work. The circuit
breaker limits repeated failures for one definition, but it does not replace
schedule controls or trigger rate limiting for otherwise valid work.

## Severity

| Level | Use when |
| --- | --- |
| **SEV-1** | The storm threatens scheduler availability, prevents critical work from running, or resembles an active API-trigger flood. |
| **SEV-2** | Queue depth grows rapidly, multiple definitions are affected, or normal service objectives are materially missed. |
| **SEV-3** | One definition is overproducing work but priority capacity and critical jobs remain healthy. |
| **SEV-4** | A bounded catch-up is draining at an acceptable rate with no customer impact. |

## Preconditions / Access needed

- A valid administrator JWT in `$TOKEN`, with the relevant `jobs:read`,
  `jobs:manage`, `jobs:trigger`, or `jobs:admin` scope.
- Network access to the scheduler API and the configured OpenTelemetry backend.
- Authority to disable/re-enable a job definition.
- Access to the approved definition/configuration deployment process for a
  schedule, misfire policy, catch-up cap, or per-definition concurrency cap.
- Access to approved worker-host controls if capacity must be drained or
  increased.
- A current list of required worker tags for high-volume definitions.

Initialize the PowerShell session:

```powershell
$BaseUri = 'http://localhost:5017'
$Headers = @{ Authorization = "Bearer $TOKEN" }
$JobDefinitionId = '<suspected-job-definition-id>'
```

## Triage

1. **Capture ready health and leader ownership.** Schedule materialisation is a
   singleton leader duty. Record this output before changing schedules or
   capacity.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/health/ready" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/leader" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. **Record the queue-depth metric in the configured OpenTelemetry backend.**
   Capture its current value and recent slope for `scheduler.queue.depth`.
   Also capture `scheduler.claim.latency`, `scheduler.runs.claimed`, and
   `scheduler.run.duration` for the same time window. No scheduler metrics
   HTTP endpoint is documented, so use the established telemetry backend rather
   than assuming a `/metrics` route exists.

3. **Inspect pending work directly through the API.** Preserve the result
   before mitigation; it is the evidence needed to identify a dominant job
   definition, correlation pattern, or due-time burst.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Pending" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

4. **Compare active and retrying work with the pending backlog.** A high
   pending count with little active work suggests insufficient eligible
   capacity, whereas a high active count can indicate downstream saturation.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Running" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Retrying" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

5. **Inspect upcoming schedule materialisation.** Look for a recurring
   definition that will continue adding a large number of due runs.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/schedule/upcoming" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

6. **Inspect all job definitions.** Identify schedule interval, enablement,
   required tags, priority, misfire behavior, catch-up policy, and any
   per-definition concurrency setting exposed by the definition representation.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/jobs" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

7. **Narrow the backlog to a suspected definition.** The documented list
   endpoint accepts `jobDefinitionId`; use it to confirm whether one
   definition accounts for the storm.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Pending&jobDefinitionId=$JobDefinitionId" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

8. **Correlate a suspected external trigger source.** If trigger callers set a
   known correlation ID, use the documented filter. For a text clue available
   in the run record, use the documented search parameter. Preserve results
   before revoking or rate-limiting a caller.

   ```powershell
   $CorrelationId = '<suspected-correlation-id>'
   $SearchText = '<known-trigger-clue>'

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?correlationId=$CorrelationId" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?search=$SearchText" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

9. **Inspect worker capacity and tag eligibility.** A queue may look like a
   storm if the only workers carrying a required tag are down, drained, or
   saturated.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/workers" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

10. **Classify the arrival pattern before selecting mitigation.**

    - **Misfire catch-up:** many runs for one recurring definition appear after
      downtime and its policy is `RunAllMissed`.
    - **Over-tight interval:** the same definition's normal interval is shorter
      than its sustainable execution/capacity rate, with no outage required.
    - **External trigger flood:** runs cluster by caller-provided correlation
      IDs, trigger evidence, or time window rather than normal schedule slots.
    - **Capacity/eligibility shortfall:** valid pending work cannot be claimed
      because matching tags or worker capacity are unavailable.

## Diagnosis table

| Possible cause | Signal | Confirm |
| --- | --- | --- |
| `RunAllMissed` catch-up after downtime | A burst from one recurring definition begins after service/leader recovery. | Inspect the definition's misfire policy, `/schedule/upcoming`, run timing, and leader/ready evidence. |
| Interval set too tightly | One enabled interval definition continuously produces work faster than it drains. | Compare its schedule configuration, pending population, run duration, claim latency, and sustainable worker capacity. |
| External trigger flooding | Runs correlate to a client, correlation ID, or abrupt request pattern rather than scheduled occurrences. | Use documented run correlation/search filters and the API access/audit evidence available in the deployment. |
| Missing tagged capacity | Backlog belongs to definitions needing tags absent from healthy workers. | Compare definition requirements with `GET /api/v1/workers`. |
| Downstream slowdown | Running work is elevated and `scheduler.run.duration` rises along with the queue. | Inspect representative run logs and downstream service health before merely adding workers. |
| Repeated failures | Retries/dead letters grow and a per-definition circuit may open. | Inspect `Retrying`/`DeadLettered` runs, DLQ, and circuit configuration. |
| Leader materialisation issue | Unexpected schedule behavior coincides with leadership loss/change. | Check `/api/v1/leader`, ready health, and `scheduler.leadership.changes`. |

## Remediation

1. **Contain the confirmed offending definition immediately.** Disabling it is
   the documented API action that stops it producing further scheduled work.
   Do this after preserving enough evidence to distinguish a catch-up from an
   external flood.

   ```powershell
   Invoke-RestMethod -Method Post `
     -Uri "$BaseUri/api/v1/jobs/$JobDefinitionId/disable" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. **Verify that the definition is no longer enabled and watch pending growth.**
   Re-read job definitions and the isolated pending list. A disabled
   definition does not remove runs that already exist; it contains future
   materialisation while existing work drains under normal scheduler control.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/jobs" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Pending&jobDefinitionId=$JobDefinitionId" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. **Correct a catch-up policy before re-enabling the definition.** For an
   outage where replaying every missed occurrence is not required, change the
   affected definition's misfire behavior from `RunAllMissed` to one of the
   intended safer policies:

   ```text
   MisfirePolicy: SkipToNext
   ```

   Use `SkipToNext` to skip missed occurrences and resume at the next normal
   slot. Use `FireNow` only when exactly one immediate recovery execution is
   appropriate:

   ```text
   MisfirePolicy: FireNow
   ```

   Apply this through the definition's established configuration/deployment
   representation. The documented endpoint list confirms `GET/POST /api/v1/jobs`
   but does not specify a safe update payload, so do not invent a partial POST
   body that could overwrite required definition fields.

4. **Cap future catch-up.** Set an approved `MaxCatchUp` limit on the affected
   schedule so a future outage cannot materialise an unbounded backlog. A
   conservative example is:

   ```text
   MaxCatchUp: 1
   ```

   Choose the production value from the business recovery requirement and
   available capacity; apply it with the same approved definition-management
   mechanism. Do not use direct SQLite updates.

5. **Fix an over-tight interval.** Increase the interval to a sustainable
   rate, or lower the definition's existing per-definition concurrency cap to
   protect downstream systems while it drains. Preserve enough worker capacity
   for unrelated critical work. The exact definition payload/schema is not
   specified by the documented API, so use the existing managed definition
   source rather than guessing a field name.

6. **Use circuit protection correctly for failing work.** The circuit breaker
   opens after `Engine:CircuitFailureThreshold=5` consecutive failures by
   default and cools down for `Engine:CircuitCooldownSeconds=60`. It prevents
   one failing definition from consuming the fleet, but it is not a primary
   throttle for successful schedule floods.

   ```text
   Engine:CircuitFailureThreshold=5
   Engine:CircuitCooldownSeconds=60
   ```

   Restore the dependency or handler root cause rather than weakening this
   guardrail to make a queue graph look smaller.

7. **Stop an external trigger flood at its source.** Retain the job disabled
   while the offending client is contained. Remove or suspend the caller's
   `jobs:trigger` authorization through the deployment's identity/access
   controls, and enforce a rate limit at the API boundary for
   `POST /api/v1/jobs/{id}/trigger`. The scheduler exposes no documented API
   endpoint to revoke JWTs or configure rate limits, so use the approved
   authentication/API-gateway control instead of fabricating a scheduler call.

8. **Drain and scale workers deliberately.** For planned capacity reduction,
   transition workers through the approved process to `WorkerStatus.Draining`
   so they stop claiming new work and finish in-flight work; no drain endpoint
   is listed by the scheduler. Add capacity only where it has the required
   tags. A standalone replacement worker can be started from the repository
   root as follows:

   ```powershell
   Set-Location 'C:\Users\rukwaropaul\Downloads\DEV\Projects\17-distributed-job-scheduler'
   dotnet run --project src/JobScheduler.Worker -- --node-id w2 --tags etl,reports
   ```

   For an API-host worker topology, apply the intended worker setting through
   normal configuration and restart the API host:

   ```text
   Node:RunWorker=true
   ```

9. **Re-enable only after the rate is safe.** Once schedule policy, catch-up
   cap, interval, trigger control, and capacity are corrected, re-enable the
   definition and observe it closely.

   ```powershell
   Invoke-RestMethod -Method Post `
     -Uri "$BaseUri/api/v1/jobs/$JobDefinitionId/enable" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

10. **Protect low-priority work while the backlog drains.** The scheduler
    orders due runs by priority with `Engine:PriorityAgingPerMinute=1.0` by
    default, boosting waiting low-priority work over time. This limits
    starvation; it does not reduce a flood's arrival rate. Keep the aging
    policy in view when selecting concurrency and capacity changes.

## Verification

1. Re-check pending work at a controlled interval. Queue depth should decline
   after containment, not merely fluctuate around the pre-mitigation peak.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Pending" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Start-Sleep -Seconds 30

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Pending" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

2. Confirm the affected definition's isolated queue drains and that it is
   re-enabled only when its corrected schedule is visible in the job definition
   and upcoming schedule response.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/runs?state=Pending&jobDefinitionId=$JobDefinitionId" `
     -Headers $Headers | ConvertTo-Json -Depth 10

   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/schedule/upcoming" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

3. Confirm enough eligible, healthy workers are present and that no planned
   drain is unexpectedly responsible for the remaining queue.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/api/v1/workers" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

4. In the configured OpenTelemetry backend, verify all of the following:

   - `scheduler.queue.depth` trends to its expected baseline.
   - `scheduler.claim.latency` recovers from the storm.
   - `scheduler.runs.claimed` shows healthy throughput rather than stalled
     capacity.
   - `scheduler.run.duration` is not rising because a downstream service is
     still saturated.
   - `scheduler.leadership.changes` is not unexpectedly flapping.

5. Confirm `/health/ready` has stable pending/running counts and a leader
   owner. Check representative low-priority runs as well as the high-priority
   workload to ensure priority aging is preventing starvation.

   ```powershell
   Invoke-RestMethod -Method Get `
     -Uri "$BaseUri/health/ready" `
     -Headers $Headers | ConvertTo-Json -Depth 10
   ```

## Prevention / Follow-up

- Require an explicit misfire choice for every recurring definition; do not
  leave `RunAllMissed` in place without a tested recovery-capacity decision.
- Set and periodically review a finite `MaxCatchUp` cap for schedules that
  could experience downtime.
- Validate that every interval is sustainable against measured
  `scheduler.run.duration`, worker/tag capacity, and expected peak rate.
- Enforce API-side rate limits for trigger calls and use least privilege:
  only trusted callers should receive the `jobs:trigger` scope.
- Treat schedule flooding as a denial-of-service risk. API rate limits,
  restricted `jobs:trigger` issuance, and `MaxCatchUp` are complementary
  controls; none alone protects every ingress path.
- Maintain per-definition concurrency caps appropriate to downstream limits and
  retain circuit-breaker protection for failure storms.
- Alert on queue-depth slope, claim latency, dominant definition, required-tag
  capacity, and leadership changes—not only an absolute queue threshold.
- Practice a controlled disable, policy update, capacity recovery, and
  re-enable sequence before a production catch-up incident.

## Escalation

Escalate to the Northstar Platform Team when:

- Queue depth keeps growing after the offending definition is disabled.
- The source cannot be distinguished between schedule materialisation and an
  external trigger flood.
- A caller continues creating work after its expected trigger access was
  contained.
- No eligible workers remain for required tags, or scaling cannot restore
  throughput.
- Claim latency, run duration, or leader instability remains elevated after
  the backlog is contained.
- The storm risks duplicate, irreversible, or compliance-sensitive external
  processing.
