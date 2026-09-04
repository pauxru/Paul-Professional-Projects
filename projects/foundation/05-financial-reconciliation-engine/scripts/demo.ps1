#requires -Version 7.0
<#
.SYNOPSIS
    End-to-end demo of the Financial Reconciliation & Settlement Engine.

.DESCRIPTION
    Builds the solution, generates a synthetic internal/external dataset with a known number of
    defects, starts the API on http://localhost:5005, then drives a full reconciliation:
    token -> import -> run -> report -> balance -> exception triage (incl. four-eyes approval).

    Requires PowerShell 7+ (uses Invoke-RestMethod -Form for multipart upload).
    The API is always stopped again in the finally block.

.EXAMPLE
    pwsh ./scripts/demo.ps1
    pwsh ./scripts/demo.ps1 -Rows 20000 -Seed 7
#>
[CmdletBinding()]
param(
    [int]$Rows = 5000,
    [int]$Seed = 42,
    [string]$BaseUrl = "http://localhost:5005"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dataDir = Join-Path $root 'data'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

$api = $null
try {
    Write-Step "1/9  Build (Release)"
    dotnet build -c Release --nologo | Out-Host

    Write-Step "2/9  Generate synthetic dataset ($Rows rows, seed $Seed)"
    dotnet run -c Release --no-build --project src/ReconEngine.DataGen -- `
        --rows $Rows --seed $Seed `
        --inject duplicates,amount-mismatch,missing,fees,refunds `
        --out $dataDir --no-fixed | Out-Host

    Write-Step "3/9  Start API on $BaseUrl"
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $BaseUrl
    # fresh database for the demo
    Remove-Item (Join-Path $root 'src/ReconEngine.Api/recon.db*') -ErrorAction SilentlyContinue
    $api = Start-Process -FilePath 'dotnet' `
        -ArgumentList @('run','-c','Release','--no-build','--project','src/ReconEngine.Api') `
        -PassThru -RedirectStandardOutput (Join-Path $artifacts 'api.out.log') `
        -RedirectStandardError (Join-Path $artifacts 'api.err.log')

    Write-Host "  API pid $($api.Id); waiting for readiness..."
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 750
        try {
            $r = Invoke-RestMethod "$BaseUrl/health/ready" -TimeoutSec 3
            if ($r) { $ready = $true; break }
        } catch { }
    }
    if (-not $ready) { throw "API did not become ready. See $artifacts\api.err.log" }
    Write-Host "  API is ready." -ForegroundColor Green

    Write-Step "4/9  Issue tokens (maker + checker for four-eyes)"
    $maker = (Invoke-RestMethod -Method Post "$BaseUrl/api/v1/auth/token" -ContentType application/json `
        -Body '{ "subject": "maker@demo", "scopes": ["recon:run","recon:resolve","recon:approve"] }').accessToken
    $checker = (Invoke-RestMethod -Method Post "$BaseUrl/api/v1/auth/token" -ContentType application/json `
        -Body '{ "subject": "checker@demo", "scopes": ["recon:resolve","recon:approve"] }').accessToken
    $mk = @{ Authorization = "Bearer $maker" }
    $ck = @{ Authorization = "Bearer $checker" }
    Write-Host "  maker + checker tokens issued."

    Write-Step "5/9  Import internal + external files"
    $imp1 = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/imports" -Headers $mk `
        -Form @{ file = Get-Item (Join-Path $dataDir 'internal.csv'); profile = 'internal-csv' }
    $imp2 = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/imports" -Headers $mk `
        -Form @{ file = Get-Item (Join-Path $dataDir 'external.csv'); profile = 'external-csv' }
    Write-Host ("  internal: {0} accepted / {1} rejected" -f $imp1.acceptedRows, $imp1.rejectedRows)
    Write-Host ("  external: {0} accepted / {1} rejected" -f $imp2.acceptedRows, $imp2.rejectedRows)

    Write-Step "6/9  Start a reconciliation run"
    $run = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/runs" -Headers $mk -ContentType application/json -Body '{}'
    Write-Host ("  run {0}" -f $run.id)
    Write-Host ("  matches={0}  exceptions={1}  carriedForward={2}  balanceOK={3}  {4}ms" -f `
        $run.matchCount, $run.exceptionCount, $run.carriedForwardCount, $run.balanceAssertionPassed, $run.durationMs) `
        -ForegroundColor Green

    Write-Step "7/9  Run report + balance assertion"
    Invoke-RestMethod "$BaseUrl/api/v1/runs/$($run.id)/report" -Headers $mk | ConvertTo-Json -Depth 6 | Out-Host
    Invoke-RestMethod "$BaseUrl/api/v1/reports/runs/$($run.id)/balance" -Headers $mk | ConvertTo-Json -Depth 6 | Out-Host

    Write-Step "8/9  Exception queue + aging"
    $open = Invoke-RestMethod "$BaseUrl/api/v1/exceptions?status=Open&pageSize=5" -Headers $mk
    Write-Host ("  open exceptions (page): {0} of {1}" -f $open.items.Count, $open.totalCount)
    Invoke-RestMethod "$BaseUrl/api/v1/reports/aging" -Headers $mk | ConvertTo-Json -Depth 6 | Out-Host

    if ($open.items.Count -gt 0) {
        $ex = $open.items[0]
        Write-Host ("  triaging exception {0} ({1}, {2} {3})" -f $ex.id, $ex.type, ($ex.amountMinor/100), $ex.currency)
        Invoke-RestMethod -Method Post "$BaseUrl/api/v1/exceptions/$($ex.id)/assign" -Headers $mk `
            -ContentType application/json -Body '{ "assignee": "maker@demo" }' | Out-Null
        Invoke-RestMethod -Method Post "$BaseUrl/api/v1/exceptions/$($ex.id)/comment" -Headers $mk `
            -ContentType application/json -Body '{ "text": "Investigated; proposing write-off." }' | Out-Null

        # Propose a write-off (maker). If the amount is at/above the threshold it needs four-eyes.
        Invoke-RestMethod -Method Post "$BaseUrl/api/v1/exceptions/$($ex.id)/resolve" -Headers $mk `
            -ContentType application/json -Body '{ "reason": "WriteOff", "note": "small residual" }' | Out-Null
        $after = Invoke-RestMethod "$BaseUrl/api/v1/exceptions/$($ex.id)" -Headers $mk
        Write-Host ("  status after maker resolve: {0}" -f $after.status)

        if ($after.status -eq 'PendingApproval') {
            Write-Host "  four-eyes: checker approves (different user)..." -ForegroundColor Yellow
            $final = Invoke-RestMethod -Method Post "$BaseUrl/api/v1/exceptions/$($ex.id)/approve" -Headers $ck
            Write-Host ("  status after checker approve: {0}" -f $final.status) -ForegroundColor Green
        }
        Write-Host "  audit trail:"
        (Invoke-RestMethod "$BaseUrl/api/v1/exceptions/$($ex.id)" -Headers $mk).auditTrail |
            ForEach-Object { "    {0}  {1}  {2}" -f $_.atUtc, $_.actor, $_.action } | Out-Host
    }

    Write-Step "9/9  OpenAPI"
    Write-Host "  OpenAPI document: $BaseUrl/openapi/v1.json"
    Write-Host "`nDemo complete." -ForegroundColor Green
}
finally {
    if ($api -and -not $api.HasExited) {
        Write-Host "`nStopping API (pid $($api.Id))..." -ForegroundColor DarkGray
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
    }
}
