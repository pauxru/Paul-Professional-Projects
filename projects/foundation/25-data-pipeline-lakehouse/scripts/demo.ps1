#requires -Version 7.0
<#
.SYNOPSIS
    End-to-end demo of the Data Pipeline & Analytics Lakehouse.

.DESCRIPTION
    Walks the full story with the REAL running API on port 5025:
        1. generate + run the medallion DAG (healthy)      -> query the marts
        2. break a data-quality rule                       -> watch the circuit breaker block gold
        3. backfill a date range                           -> query the marts again

    Everything runs on the local filesystem with SQLite. No Docker, no external services.
    Demo data is fictional (Contoso Retail). Nothing is provisioned anywhere.

.NOTES
    Run from the project root:  pwsh -File scripts\demo.ps1
#>
[CmdletBinding()]
param(
    [int]    $Port      = 5025,
    [string] $DemoRoot  = "_demo"
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$apiProj = Join-Path $root 'src\Lakehouse.Api'
$apiDll  = Join-Path $apiProj 'bin\Release\net10.0\Lakehouse.Api.dll'
$base    = "http://localhost:$Port"

function Write-Banner([string]$text) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
}

function Start-Api([hashtable]$EnvVars) {
    foreach ($k in $EnvVars.Keys) { Set-Item "env:$k" $EnvVars[$k] }
    $env:Lakehouse__SeedOnStartup = 'true'
    # Launch the built DLL directly (NOT `dotnet run`) so $proc IS the API process and Stop-Process
    # kills it cleanly — `dotnet run` forks a child that would keep holding the port. The working
    # directory must be the output folder so appsettings.json (Urls=:5025) + wwwroot are found; the
    # lake/serving paths passed in EnvVars are absolute so they are unaffected.
    $outDir = Split-Path -Parent $apiDll
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) `
        -WorkingDirectory $outDir -PassThru -WindowStyle Hidden
    # wait for liveness
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-RestMethod "$base/health" -TimeoutSec 2 | Out-Null; return $proc } catch { Start-Sleep 1 }
    }
    throw "API did not become healthy on $base within 60s"
}

function Stop-Api($proc) {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep 2
}

function Token([string]$role) {
    (Invoke-RestMethod -Method Post "$base/api/auth/token" `
        -Body (@{ role = $role } | ConvertTo-Json) -ContentType 'application/json').token
}

# ---- build once -------------------------------------------------------------------------------------
Write-Banner 'Building the API (Release)'
dotnet build -c Release $apiProj | Out-Host
if (-not (Test-Path $apiDll)) { throw "API build output not found at $apiDll" }

# fresh demo dirs
$healthyRoot = Join-Path $root "$DemoRoot\healthy"
$breakRoot   = Join-Path $root "$DemoRoot\break"
foreach ($d in @($healthyRoot, $breakRoot)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

$smallGen = @{
    'Lakehouse__Generator__Customers' = '80'
    'Lakehouse__Generator__Products'  = '40'
    'Lakehouse__Generator__Orders'    = '600'
    'Lakehouse__Generator__Sessions'  = '300'
    'Lakehouse__Generator__Days'      = '30'
}

# =====================================================================================================
Write-Banner '1) GENERATE + RUN THE DAG (healthy)'
$proc = $null
try {
    $envH = $smallGen.Clone()
    $envH['Lakehouse__LakeRoot']              = (Join-Path $healthyRoot 'lake')
    $envH['Lakehouse__ServingDbPath']         = (Join-Path $healthyRoot 'serving\serving.db')
    $envH['Lakehouse__Generator__DefectRate'] = '0.03'
    $proc = Start-Api $envH
    $r = Token 'reader'; $rh = @{ Authorization = "Bearer $r" }

    Write-Host "`n-- DAG topological order --" -ForegroundColor Yellow
    (Invoke-RestMethod "$base/api/pipeline/dag" -Headers $rh).order -join '  ->  ' | Write-Host

    Write-Host "`n-- Latest run (per-task) --" -ForegroundColor Yellow
    $runs = Invoke-RestMethod "$base/api/pipeline/runs" -Headers $rh
    $last = if ($runs -is [array]) { $runs[0] } else { $runs }
    "run={0}  success={1}  rowsOut={2}  failed={3}  blocked={4}  durationMs={5:N0}" -f `
        $last.runId, $last.success, $last.totalRowsOut, $last.failed, $last.blocked, $last.durationMs | Write-Host

    Write-Host "`n-- Data-quality (should be GREEN) --" -ForegroundColor Yellow
    Invoke-RestMethod "$base/api/quality/latest" -Headers $rh | ConvertTo-Json -Depth 4 | Write-Host

    Write-Host "`n-- Marts: daily revenue (USD) --" -ForegroundColor Yellow
    Invoke-RestMethod -Method Post "$base/api/sql" -Headers $rh -ContentType 'application/json' `
        -Body (@{ sql = 'SELECT date_key, orders, revenue_usd FROM agg_daily_revenue ORDER BY date_key LIMIT 5' } | ConvertTo-Json) |
        ConvertTo-Json -Depth 5 | Write-Host
}
finally { Stop-Api $proc }

# =====================================================================================================
Write-Banner '2) BREAK A QUALITY RULE -> CIRCUIT BREAKER BLOCKS GOLD'
$proc = $null
try {
    # pre-seed the quarantine baseline low, then flood with defects: the reject-volume anomaly (Fail)
    # trips the breaker so gold is never promoted.
    $cp = Join-Path $breakRoot 'lake\_checkpoints'
    New-Item -ItemType Directory -Path $cp -Force | Out-Null
    Set-Content -Path (Join-Path $cp 'dq_quarantine_baseline.txt') -Value '1' -NoNewline

    $envB = $smallGen.Clone()
    $envB['Lakehouse__LakeRoot']              = (Join-Path $breakRoot 'lake')
    $envB['Lakehouse__ServingDbPath']         = (Join-Path $breakRoot 'serving\serving.db')
    $envB['Lakehouse__Generator__DefectRate'] = '0.35'   # flood of bad rows
    $proc = Start-Api $envB
    $r = Token 'reader'; $rh = @{ Authorization = "Bearer $r" }

    Write-Host "`n-- Data-quality (expect a BLOCKING failure on silver) --" -ForegroundColor Yellow
    Invoke-RestMethod "$base/api/quality/latest" -Headers $rh | ConvertTo-Json -Depth 4 | Write-Host

    Write-Host "`n-- Run history (expect dq_silver Failed, gold tasks Blocked) --" -ForegroundColor Yellow
    $runs = Invoke-RestMethod "$base/api/pipeline/runs" -Headers $rh
    $last = if ($runs -is [array]) { $runs[0] } else { $runs }
    $last.tasks | Where-Object { $_.state -ne 'Succeeded' } |
        ForEach-Object { "  {0,-26} {1}" -f $_.taskId, $_.state } | Write-Host

    Write-Host "`n-- Serving tables (fact_order_line should be ABSENT — gold was blocked) --" -ForegroundColor Yellow
    (Invoke-RestMethod "$base/api/sql/tables" -Headers $rh) -join ', ' | Write-Host
}
finally { Stop-Api $proc }

# =====================================================================================================
Write-Banner '3) BACKFILL A DATE RANGE -> QUERY THE MARTS'
$proc = $null
try {
    # reuse the healthy lake (gold persists -> seed is skipped); backfill re-runs a window idempotently
    $envH = $smallGen.Clone()
    $envH['Lakehouse__LakeRoot']              = (Join-Path $healthyRoot 'lake')
    $envH['Lakehouse__ServingDbPath']         = (Join-Path $healthyRoot 'serving\serving.db')
    $envH['Lakehouse__Generator__DefectRate'] = '0.03'
    $proc = Start-Api $envH
    $op = Token 'operator'; $oh = @{ Authorization = "Bearer $op" }
    $r  = Token 'reader';   $rh = @{ Authorization = "Bearer $r" }

    Write-Host "`n-- Backfill 2026-01-01..2026-01-07 (per-day windows) --" -ForegroundColor Yellow
    Invoke-RestMethod -Method Post "$base/api/pipeline/backfill?from=2026-01-01&to=2026-01-07" -Headers $oh |
        ForEach-Object { "  window={0}  success={1}  rowsOut={2}" -f $_.window, $_.success, $_.totalRowsOut } | Write-Host

    Write-Host "`n-- Marts after backfill: revenue by channel (metrics layer) --" -ForegroundColor Yellow
    Invoke-RestMethod -Method Post "$base/api/metrics/query" -Headers $rh -ContentType 'application/json' `
        -Body (@{ metric = 'revenue_usd'; dimensions = @('channel'); grain = 'Month' } | ConvertTo-Json) |
        ConvertTo-Json -Depth 5 | Write-Host

    Write-Host "`n-- Injection / write attempt is rejected (expect HTTP 400) --" -ForegroundColor Yellow
    try {
        Invoke-RestMethod -Method Post "$base/api/sql" -Headers $rh -ContentType 'application/json' `
            -Body (@{ sql = 'DROP TABLE fact_order_line' } | ConvertTo-Json) | Out-Null
        Write-Host '  UNEXPECTED: write was not rejected!' -ForegroundColor Red
    } catch {
        Write-Host "  Rejected as expected: $($_.Exception.Response.StatusCode)" -ForegroundColor Green
    }
}
finally { Stop-Api $proc }

Write-Banner 'DEMO COMPLETE'
Write-Host 'Healthy lake:  ' $healthyRoot
Write-Host 'Broken lake:   ' $breakRoot
Write-Host 'Start the API for interactive exploration:  dotnet run -c Release --project src\Lakehouse.Api'
