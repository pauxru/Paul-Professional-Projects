<#
  demo.ps1 — Intelligent Document Processing Platform
  End-to-end demonstration: build, launch the API (seeded corpus), then exercise the pipeline via the
  real HTTP API — authenticate, read the STP KPI, inspect an auto-approved document, work a review
  task (claim -> correct -> approve), and list exports.

  Runs entirely offline with only the .NET SDK. No Docker, no external services, no paid APIs.

  Usage:
    ./scripts/demo.ps1                 # builds, starts the API, runs the demo, stops the API
    ./scripts/demo.ps1 -SkipBuild      # skip the build step
    ./scripts/demo.ps1 -BaseUrl http://localhost:5009 -NoStart   # use an already-running API
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5009",
    [switch]$SkipBuild,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Write-Step($n, $msg) { Write-Host "`n=== $n. $msg ===" -ForegroundColor Cyan }

if (-not $SkipBuild) {
    Write-Step 0 "Build (Release)"
    dotnet build -c Release --nologo | Out-Host
}

$apiProc = $null
if (-not $NoStart) {
    Write-Step 1 "Start API (Development, seeds the 19-document corpus)"
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:ASPNETCORE_URLS = $BaseUrl
    $env:Seed = "true"
    $apiProc = Start-Process -FilePath "dotnet" `
        -ArgumentList "run","--project","src/Idp.Api","-c","Release","--no-build" `
        -PassThru -WindowStyle Hidden
    Write-Host "Waiting for $BaseUrl/health ..." -ForegroundColor DarkGray
    $ready = $false
    foreach ($i in 1..40) {
        try { Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 2 | Out-Null; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 750 }
    }
    if (-not $ready) { throw "API did not become healthy at $BaseUrl" }
    Write-Host "API is healthy." -ForegroundColor Green
}

try {
    Write-Step 2 "Authenticate (dev token)"
    $token = (Invoke-RestMethod -Method Post "$BaseUrl/api/v1/dev/token" `
        -ContentType application/json -Body '{"subject":"demo"}').access_token
    $h = @{ Authorization = "Bearer $token" }
    Write-Host "Got a bearer token." -ForegroundColor Green

    Write-Step 3 "Straight-through-processing KPI"
    $stp = Invoke-RestMethod "$BaseUrl/api/v1/metrics/stp" -Headers $h
    $stp | Format-List
    Write-Host ("STP rate = {0:P2}  ({1}/{2} auto-approved, {3} in review)" -f `
        $stp.straightThroughRate, $stp.autoApproved, $stp.totalDocuments, $stp.inReview) -ForegroundColor Yellow

    Write-Step 4 "An auto-approved document and its extracted fields"
    $docs = Invoke-RestMethod "$BaseUrl/api/v1/documents?page=1&pageSize=50" -Headers $h
    $auto = $docs.items | Where-Object routing -eq "AutoApproved" | Select-Object -First 1
    if ($auto) {
        Invoke-RestMethod "$BaseUrl/api/v1/documents/$($auto.id)/fields" -Headers $h |
            Select-Object fieldKey, value, confidence, strategy | Format-Table -AutoSize
    } else { Write-Host "No auto-approved document found." -ForegroundColor DarkYellow }

    Write-Step 5 "Work a review task: claim -> correct -> approve"
    $q = Invoke-RestMethod "$BaseUrl/api/v1/review/queue" -Headers $h
    if ($q.items.Count -gt 0) {
        $task = $q.items[0]
        Write-Host "Claiming task $($task.taskId) (document value $($task.documentValue))..."
        Invoke-RestMethod -Method Post "$BaseUrl/api/v1/review/$($task.taskId)/claim" `
            -Headers $h -ContentType application/json -Body '{"reviewer":"demo"}' | Out-Null
        $doc = Invoke-RestMethod "$BaseUrl/api/v1/documents/$($task.documentId)" -Headers $h
        Write-Host "Failing/warning validations:" -ForegroundColor DarkGray
        $doc.validations | Where-Object outcome -ne "Pass" |
            Select-Object ruleName, outcome, message | Format-Table -AutoSize
        Invoke-RestMethod -Method Post "$BaseUrl/api/v1/review/$($task.taskId)/approve" `
            -Headers $h -ContentType application/json -Body '{"reviewer":"demo"}' | Out-Null
        Write-Host "Approved task $($task.taskId)." -ForegroundColor Green
    } else { Write-Host "Review queue is empty." -ForegroundColor DarkYellow }

    Write-Step 6 "Exports"
    Invoke-RestMethod "$BaseUrl/api/v1/exports" -Headers $h |
        Select-Object documentId, status, attempts, erpReference | Format-Table -AutoSize

    Write-Host "`nDemo complete." -ForegroundColor Green
}
finally {
    if ($apiProc -and -not $apiProc.HasExited) {
        Write-Host "`nStopping API (PID $($apiProc.Id))..." -ForegroundColor DarkGray
        Stop-Process -Id $apiProc.Id -Force
    }
}
