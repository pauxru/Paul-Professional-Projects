# Distributed Job Scheduler Demo Walkthrough

This walkthrough demonstrates the coordination protocol, normal execution, failure recovery, dead-letter replay, and leader visibility. Run commands from:

```powershell
Set-Location 'C:\Users\rukwaropaul\Downloads\DEV\Projects\17-distributed-job-scheduler'
```

All names below include a generated suffix so the demo can be repeated without colliding with an existing job definition.

## 1. Start the API on port 5017

Open a PowerShell terminal and keep it running:

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:5017'
dotnet run --project .\src\JobScheduler.Api
```

Expected output includes a listener at `http://localhost:5017`. The API host also runs its configured embedded worker (`api-embedded`), which makes the first, single-worker execution easy to observe.

## 2. Open the dashboard

Browse to:

```text
http://localhost:5017/
```

Expected view: job definitions, upcoming schedule, recent runs, worker nodes, dead-letter queue, and the current leader. The dashboard obtains a development token automatically when running outside Production.

## 3. Mint a development JWT and prepare API headers

The token endpoint is for development/demo use only; it is disabled in Production.

### PowerShell

```powershell
$base = 'http://localhost:5017'
$tokenResponse = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/api/v1/auth/token" `
  -ContentType 'application/json' `
  -Body (@{
    subject = 'portfolio-demo'
    scopes  = @('jobs:read', 'jobs:trigger', 'jobs:manage', 'jobs:admin')
  } | ConvertTo-Json -Compress)

$token = $tokenResponse.accessToken
$headers = @{ Authorization = "Bearer $token" }
$tokenResponse | Select-Object tokenType, expiresAt, scopes
```

Expected output: `tokenType` is `Bearer`, with the requested scheduler scopes.

### curl alternative

```powershell
curl.exe -sS -X POST "$base/api/v1/auth/token" `
  -H "Content-Type: application/json" `
  -d '{"subject":"portfolio-demo","scopes":["jobs:read","jobs:trigger","jobs:manage","jobs:admin"]}'
```

The response contains `accessToken`; use it as `Authorization: Bearer <accessToken>` for subsequent curl requests.

## 4. Create a job definition

Create a short slow job so its state transitions are visible. `slow` is one of the fixed, registered demo handlers; payloads cannot select arbitrary code or shell commands.

```powershell
$suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
$stateJobRequest = @{
  name             = "portfolio-state-$suffix"
  handlerType      = 'slow'
  payloadJson      = '{"durationSeconds":8}'
  queue            = 'reports'
  priority         = 5
  concurrencyLimit = 1
  owner            = 'Northstar Platform Team (fictional)'
  triggerType      = 'Manual'
  maxAttempts      = 1
  timeoutSeconds   = 30
} | ConvertTo-Json -Compress

$stateJob = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/api/v1/jobs" `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body $stateJobRequest

$stateJob | Select-Object id, name, handlerType, queue, triggerType
```

Expected output: an HTTP `201 Created` response represented as an object with a new `id` and `handlerType` of `slow`.

## 5. Trigger it and watch `Pending` → `Running` → `Succeeded`

```powershell
$stateRun = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/api/v1/jobs/$($stateJob.id)/trigger" `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body (@{
    idempotencyKey = "portfolio-state-$suffix"
    correlationId  = "portfolio-demo-$suffix"
  } | ConvertTo-Json -Compress)

$terminalStates = @('Succeeded', 'Failed', 'TimedOut', 'Cancelled', 'DeadLettered')
do {
  $runView = Invoke-RestMethod -Uri "$base/api/v1/runs/$($stateRun.id)" -Headers $headers
  $runView | Select-Object id, state, attemptCount, leaseOwner, fencingToken, startedAt, finishedAt
  Start-Sleep -Seconds 1
} until ($runView.state -in $terminalStates)

Invoke-RestMethod -Uri "$base/api/v1/runs/$($stateRun.id)/logs" -Headers $headers
```

Expected lifecycle: `Pending`, a brief `Claimed`, `Running`, then `Succeeded`. `Claimed` is an intentional intermediate state and may be too short to see in a one-second poll. The final run includes the correlation ID supplied on trigger and its execution logs.

## 6. Start two external worker processes

Open two additional PowerShell terminals at the repository root.

**Worker terminal 1**

```powershell
dotnet run --project .\src\JobScheduler.Worker -- --node-id w1 --tags etl,reports
```

**Worker terminal 2**

```powershell
dotnet run --project .\src\JobScheduler.Worker -- --node-id w2 --tags etl,reports
```

Expected dashboard/API result: `w1` and `w2` register and heartbeat. Confirm them directly:

```powershell
Invoke-RestMethod -Uri "$base/api/v1/workers" -Headers $headers |
  Select-Object nodeId, status, tags, maxConcurrency, heartbeatAgeSeconds
```

For a controlled **forced** stop later, launch the same two commands from a controller PowerShell session and retain their process IDs:

```powershell
$repo = 'C:\Users\rukwaropaul\Downloads\DEV\Projects\17-distributed-job-scheduler'
$w1 = Start-Process -FilePath dotnet -WorkingDirectory $repo `
  -ArgumentList 'run --project .\src\JobScheduler.Worker -- --node-id w1 --tags etl,reports' -PassThru
$w2 = Start-Process -FilePath dotnet -WorkingDirectory $repo `
  -ArgumentList 'run --project .\src\JobScheduler.Worker -- --node-id w2 --tags etl,reports' -PassThru
```

Use either the visible worker terminals or the tracked-process form, not both for the same node IDs.

## 7. Demonstrate lease expiry, reclaim, and fencing

Create a longer `slow` job whose `etl` requirement excludes the API's untagged embedded worker while allowing both `w1` and `w2` to claim it.

```powershell
$reclaimJobRequest = @{
  name             = "portfolio-reclaim-$suffix"
  handlerType      = 'slow'
  payloadJson      = '{"durationSeconds":45}'
  queue            = 'etl'
  tags             = @('etl')
  priority         = 10
  concurrencyLimit = 1
  owner            = 'Northstar Platform Team (fictional)'
  triggerType      = 'Manual'
  maxAttempts      = 1
  timeoutSeconds   = 90
} | ConvertTo-Json -Compress

$reclaimJob = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/api/v1/jobs" `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body $reclaimJobRequest

$reclaimRun = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/api/v1/jobs/$($reclaimJob.id)/trigger" `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body (@{
    idempotencyKey = "portfolio-reclaim-$suffix"
    correlationId  = "portfolio-reclaim-$suffix"
  } | ConvertTo-Json -Compress)

do {
  $beforeFailure = Invoke-RestMethod -Uri "$base/api/v1/runs/$($reclaimRun.id)" -Headers $headers
  $beforeFailure | Select-Object state, leaseOwner, fencingToken, startedAt, finishedAt
  Start-Sleep -Seconds 1
} until ($beforeFailure.state -eq 'Running' -and $beforeFailure.leaseOwner -in @('w1', 'w2'))

$victim = $beforeFailure.leaseOwner
$oldFence = $beforeFailure.fencingToken
"Force-stopping $victim at fencing token $oldFence"
```

Force-stop **the worker that owns the run**. Do not use `Ctrl+C` for this failure simulation: graceful shutdown drains in-flight work instead of modelling a stalled process.

```powershell
if ($victim -eq 'w1') {
  Stop-Process -Id $w1.Id -Force
} else {
  Stop-Process -Id $w2.Id -Force
}
```

If the workers were started in visible terminals, use the specific `dotnet` process ID for the owning worker with the same `Stop-Process -Id <pid> -Force` command.

Now poll the original run:

```powershell
do {
  $afterFailure = Invoke-RestMethod -Uri "$base/api/v1/runs/$($reclaimRun.id)" -Headers $headers
  $afterFailure | Select-Object state, leaseOwner, fencingToken, startedAt, finishedAt
  Start-Sleep -Seconds 1
} until ($afterFailure.state -eq 'Succeeded')

"Fencing token: $oldFence -> $($afterFailure.fencingToken)"
```

Expected story:

1. One external worker owns the `Running` run with fencing token `n`.
2. Its forced stop ends run heartbeats.
3. After the configured lease expires, the leader reaper returns the run to `Pending`.
4. The surviving worker claims it, receives a token greater than `n`, and completes it as `Succeeded`.

The forced-stop demo shows recovery and the fencing-token bump. If the original worker had instead paused and later resumed, its completion would be rejected by the fenced completion predicate (`WHERE FencingToken=@token AND State=Running`); it cannot overwrite the surviving worker's result.

## 8. Show the DLQ and replay path

Create a deliberately failing job with one permitted attempt:

```powershell
$dlqJobRequest = @{
  name             = "portfolio-dlq-$suffix"
  handlerType      = 'flaky'
  payloadJson      = '{"failTimes":99}'
  queue            = 'default'
  priority         = 1
  concurrencyLimit = 1
  owner            = 'Northstar Platform Team (fictional)'
  triggerType      = 'Manual'
  retryStrategy    = 'Fixed'
  retryBaseSeconds = 1
  maxAttempts      = 1
  timeoutSeconds   = 30
} | ConvertTo-Json -Compress

$dlqJob = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/api/v1/jobs" `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body $dlqJobRequest

$dlqRun = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/api/v1/jobs/$($dlqJob.id)/trigger" `
  -Headers $headers `
  -ContentType 'application/json' `
  -Body (@{ idempotencyKey = "portfolio-dlq-$suffix" } | ConvertTo-Json -Compress)

do {
  $dlqRunView = Invoke-RestMethod -Uri "$base/api/v1/runs/$($dlqRun.id)" -Headers $headers
  Start-Sleep -Seconds 1
} until ($dlqRunView.state -eq 'DeadLettered')

$dlqEntry = (Invoke-RestMethod -Uri "$base/api/v1/dlq?includeReplayed=false&pageSize=100" -Headers $headers).items |
  Where-Object { $_.jobRunId -eq $dlqRun.id } |
  Select-Object -First 1

$dlqEntry | Select-Object id, jobName, reason, error, attemptCount, deadLetteredAt

Invoke-RestMethod `
  -Method Post `
  -Uri "$base/api/v1/dlq/$($dlqEntry.id)/replay" `
  -Headers $headers |
  Select-Object id, state, attemptCount, scheduledAt
```

Expected result: the run reaches `DeadLettered`, a DLQ entry appears, and replay resets the run to a fresh pending attempt. Because this particular payload is deliberately still configured to fail, it will dead-letter again unless the definition is changed; that makes the replay action observable without pretending it repairs a bad payload.

## 9. Inspect leader election

```powershell
Invoke-RestMethod -Uri "$base/api/v1/leader" -Headers $headers |
  Format-List owner, fencingToken, acquiredAt, expiresAt, isHeld
```

Expected output identifies the node holding the current lease, its fencing token, expiry, and whether the lease is currently held. That leader performs schedule materialisation, expired-lease reaping, and retention pruning.

## Automated version

Use `scripts/demo.ps1` for the repeatable version of this sequence. It automates the API, two workers, forced worker termination, and reclaim demonstration.
