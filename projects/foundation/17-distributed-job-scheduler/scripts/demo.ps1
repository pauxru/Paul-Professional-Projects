<#
.SYNOPSIS
    End-to-end demo of the Distributed Job Scheduler: starts the API + two worker
    processes, triggers a long-running job, kills the worker that claimed it, and
    shows another worker reclaim and finish it (lease expiry + fencing in action).

.DESCRIPTION
    Zero external infrastructure. All three processes share ONE SQLite database, so
    they coordinate exactly like separate nodes would. The API runs the leader
    (materialise / reap expired leases / retention) but not a worker loop; the two
    worker processes do the claiming.

    Sequence:
      1. build once (Release)
      2. start API on http://localhost:5017 (leader only)
      3. mint a JWT, create a 'slow' job definition, trigger a run
      4. start workers w1 and w2
      5. wait until a worker claims the run (State=Running, LeaseOwner set)
      6. KILL that worker mid-run
      7. watch the surviving worker reclaim after the lease expires and Succeed
      8. print a before/after summary, then clean up every process

.NOTES
    Requires the .NET 10 SDK on PATH. Run from anywhere:  ./scripts/demo.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 5017,
    [int]$DurationSeconds = 25,
    [int]$OverallTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Paths -------------------------------------------------------------------
$root      = Split-Path -Parent $PSScriptRoot
$apiProj   = Join-Path $root 'src\JobScheduler.Api\JobScheduler.Api.csproj'
$workerProj= Join-Path $root 'src\JobScheduler.Worker\JobScheduler.Worker.csproj'
$demoDir   = Join-Path $root '.demo'
$logDir    = Join-Path $demoDir 'logs'
$dbPath    = Join-Path $demoDir 'demo.db'
$baseUrl   = "http://localhost:$Port"

# Track child processes so the finally block can always tear them down.
$procs   = @{}   # name -> System.Diagnostics.Process
$sw      = [System.Diagnostics.Stopwatch]::StartNew()

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Info($msg) { Write-Host "    $msg" -ForegroundColor Gray }

function Start-Node {
    param([string]$Name, [string]$Project, [string[]]$AppArgs)
    $out = Join-Path $logDir "$Name.out.log"
    $err = Join-Path $logDir "$Name.err.log"
    $argList = @('run', '--project', $Project, '-c', 'Release', '--no-build', '--no-restore', '--') + $AppArgs
    $p = Start-Process -FilePath 'dotnet' -ArgumentList $argList -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden
    $procs[$Name] = $p
    Write-Info "$Name started (pid $($p.Id)) -> $out"
    return $p
}

function Stop-Node {
    param([string]$Name)
    if ($procs.ContainsKey($Name) -and $procs[$Name] -and -not $procs[$Name].HasExited) {
        try { Stop-Process -Id $procs[$Name].Id -Force -ErrorAction SilentlyContinue } catch { }
        Write-Info "$Name (pid $($procs[$Name].Id)) stopped."
    }
}

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body, [string]$Token)
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $uri = "$baseUrl$Path"
    if ($null -ne $Body) {
        $json = ($Body | ConvertTo-Json -Depth 8 -Compress)
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -Body $json -ContentType 'application/json'
    }
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers
}

try {
    # --- Clean slate ---------------------------------------------------------
    Write-Step 'Preparing demo workspace'
    if (Test-Path $demoDir) { Remove-Item $demoDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    Write-Info "Shared SQLite DB: $dbPath"

    # Shared environment for every node (child processes inherit this).
    $env:ASPNETCORE_ENVIRONMENT       = 'Development'
    $env:Database__ConnectionString   = "Data Source=$dbPath;Cache=Shared"

    # --- Build once ----------------------------------------------------------
    Write-Step 'Building (Release) once so the three nodes can share output'
    dotnet build (Join-Path $root 'JobScheduler.slnx') -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

    # --- Start the API (leader only; no in-process worker) -------------------
    Write-Step "Starting API on $baseUrl (leader duties only)"
    $env:ASPNETCORE_URLS   = $baseUrl
    $env:Node__RunWorker   = 'false'
    $env:Node__RunLeader   = 'true'
    $env:Node__NodeId      = 'api'
    $env:Seed__DemoData    = 'false'
    Start-Node -Name 'api' -Project $apiProj -AppArgs @() | Out-Null

    # Wait for readiness.
    Write-Info 'Waiting for /health/ready ...'
    $ready = $false
    while (-not $ready -and $sw.Elapsed.TotalSeconds -lt 90) {
        Start-Sleep -Seconds 2
        try {
            $r = Invoke-RestMethod -Method Get -Uri "$baseUrl/health/ready" -TimeoutSec 4
            if ($r.status -eq 'ready') { $ready = $true }
        } catch { }
    }
    if (-not $ready) { throw 'API did not become ready in time.' }
    Write-Info 'API is ready.'

    # Workers should NOT inherit the API-only role override.
    Remove-Item Env:\Node__RunWorker -ErrorAction SilentlyContinue
    Remove-Item Env:\Node__NodeId    -ErrorAction SilentlyContinue

    # --- Mint a token --------------------------------------------------------
    Write-Step 'Minting a JWT (all scopes)'
    $tokenResp = Invoke-Api -Method Post -Path '/api/v1/auth/token' -Body @{
        subject = 'demo-operator'
        scopes  = @('jobs:read', 'jobs:trigger', 'jobs:manage', 'jobs:admin')
    }
    $token = $tokenResp.accessToken
    Write-Info "Token acquired (expires $($tokenResp.expiresAt))."

    # --- Create a slow job definition ---------------------------------------
    Write-Step "Creating a 'slow' job definition (~${DurationSeconds}s)"
    $jobName = "demo-slow-$(Get-Date -Format 'HHmmss')"
    $def = Invoke-Api -Method Post -Path '/api/v1/jobs' -Token $token -Body @{
        name          = $jobName
        handlerType   = 'slow'
        payloadJson   = "{`"durationSeconds`":$DurationSeconds}"
        queue         = 'default'
        priority      = 5
        maxAttempts   = 3
        timeoutSeconds= 300
        triggerType   = 'Manual'
        owner         = 'demo'
    }
    Write-Info "Definition $($def.id) '$($def.name)' created."

    # --- Trigger a run -------------------------------------------------------
    Write-Step 'Triggering a run'
    $run = Invoke-Api -Method Post -Path "/api/v1/jobs/$($def.id)/trigger" -Token $token -Body @{}
    $runId = $run.id
    Write-Info "Run $runId is $($run.state)."

    # --- Start two workers ---------------------------------------------------
    Write-Step 'Starting workers w1 and w2'
    Start-Node -Name 'w1' -Project $workerProj -AppArgs @('--node-id', 'w1', '--tags', 'etl,reports') | Out-Null
    Start-Node -Name 'w2' -Project $workerProj -AppArgs @('--node-id', 'w2', '--tags', 'etl,reports') | Out-Null

    # --- Wait for a worker to claim the run ----------------------------------
    Write-Step 'Waiting for a worker to claim the run (State=Running)'
    $firstOwner = $null
    while (-not $firstOwner -and $sw.Elapsed.TotalSeconds -lt $OverallTimeoutSeconds) {
        Start-Sleep -Seconds 2
        $cur = Invoke-Api -Method Get -Path "/api/v1/runs/$runId" -Token $token
        Write-Info "state=$($cur.state) owner=$($cur.leaseOwner) attempt=$($cur.attemptCount) fencing=$($cur.fencingToken)"
        if ($cur.state -eq 'Running' -and $cur.leaseOwner) { $firstOwner = $cur.leaseOwner }
    }
    if (-not $firstOwner) { throw 'No worker claimed the run in time.' }
    Write-Host "    -> Claimed by $firstOwner (fencing token $($cur.fencingToken))." -ForegroundColor Green

    # --- Kill the claiming worker mid-run ------------------------------------
    Write-Step "KILLING the worker that owns the run: $firstOwner"
    Stop-Node -Name $firstOwner
    Write-Info "Lease will expire (~30s); the leader's reaper resets the run to Pending; the survivor reclaims."

    # --- Watch the survivor reclaim and finish -------------------------------
    Write-Step 'Waiting for reclaim + completion'
    $final = $null
    while (-not $final -and $sw.Elapsed.TotalSeconds -lt $OverallTimeoutSeconds) {
        Start-Sleep -Seconds 3
        $cur = Invoke-Api -Method Get -Path "/api/v1/runs/$runId" -Token $token
        Write-Info "state=$($cur.state) owner=$($cur.leaseOwner) attempt=$($cur.attemptCount) fencing=$($cur.fencingToken)"
        if ($cur.state -in @('Succeeded', 'Failed', 'DeadLettered', 'Cancelled')) { $final = $cur }
    }
    if (-not $final) { throw 'Run did not reach a terminal state in time.' }

    # --- Summary -------------------------------------------------------------
    Write-Step 'RESULT'
    Write-Host "    Run id            : $runId"
    Write-Host "    First owner       : $firstOwner  (killed mid-run)"
    Write-Host "    Final owner       : $($final.leaseOwner)"
    Write-Host "    Final state       : $($final.state)"
    Write-Host "    Attempts          : $($final.attemptCount)"
    Write-Host "    Final fencing tok : $($final.fencingToken)"
    if ($final.state -eq 'Succeeded' -and $final.leaseOwner -ne $firstOwner) {
        Write-Host "`n    SUCCESS: the run was reclaimed by a different node and completed. Fencing kept it consistent." -ForegroundColor Green
    } elseif ($final.state -eq 'Succeeded') {
        Write-Host "`n    Run succeeded. (Reclaim may have landed on the same node id if timing overlapped.)" -ForegroundColor Yellow
    } else {
        Write-Host "`n    Run reached $($final.state). Inspect $logDir for node output." -ForegroundColor Yellow
    }
}
finally {
    Write-Step 'Tearing down'
    foreach ($name in @('w1', 'w2', 'api')) { Stop-Node -Name $name }
    # Clear demo-only environment variables from this shell.
    foreach ($v in 'ASPNETCORE_ENVIRONMENT','ASPNETCORE_URLS','Database__ConnectionString',
                    'Node__RunWorker','Node__RunLeader','Node__NodeId','Seed__DemoData') {
        Remove-Item "Env:\$v" -ErrorAction SilentlyContinue
    }
    Write-Info "Done in $([int]$sw.Elapsed.TotalSeconds)s. Logs: $logDir"
}
