#requires -Version 7.0
<#
.SYNOPSIS
    Demo script: generate a small synthetic stream, score it, inspect alerts,
    open a case, note it, propose disposition, and produce the tuning report.

.DESCRIPTION
    Assumes the API is running on http://localhost:5015 (see README quick-start).
    Usage:
        pwsh -File .\scripts\demo.ps1
        pwsh -File .\scripts\demo.ps1 -BaseUrl http://localhost:5015 -Count 25

.NOTES
    All data is synthetic. This script does not contact any real payment
    processor and does not read any real cardholder information.
#>

[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5015',
    [int]$Count = 25,
    [string]$Subject = 'demo-analyst',
    [int]$WaitSeconds = 5
)

$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Section([string]$title) {
    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor Cyan
    Write-Host " $title" -ForegroundColor Cyan
    Write-Host ('=' * 72) -ForegroundColor Cyan
}

function Get-Token([string]$sub, [string[]]$scopes) {
    $body = @{ subject = $sub; scopes = $scopes } | ConvertTo-Json -Compress
    $res  = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/token" `
        -ContentType 'application/json' -Body $body
    return $res.accessToken
}

function Invoke-Auth([string]$method, [string]$path, [string]$token, $body) {
    $headers = @{ Authorization = "Bearer $token" }
    if ($null -ne $body) {
        $json = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Depth 8 -Compress }
        return Invoke-RestMethod -Method $method -Uri "$BaseUrl$path" -Headers $headers `
            -ContentType 'application/json' -Body $json
    }
    return Invoke-RestMethod -Method $method -Uri "$BaseUrl$path" -Headers $headers
}

# ---------------------------------------------------------------------------
# 0. Wait for the API
# ---------------------------------------------------------------------------
Write-Section '0. Waiting for API to be live'
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$live = $false
while ((Get-Date) -lt $deadline) {
    try {
        Invoke-RestMethod -Uri "$BaseUrl/health/live" -Method Get -TimeoutSec 2 | Out-Null
        $live = $true; break
    } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $live) {
    Write-Warning "API not reachable at $BaseUrl. Start it with: dotnet run --project src/FraudPipeline.Api -c Release"
    exit 1
}
Write-Host "  API is live at $BaseUrl" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 1. Issue tokens (dev endpoint)
# ---------------------------------------------------------------------------
Write-Section '1. Issuing demo tokens'
$scoreTok    = Get-Token $Subject @('risk:score')
$investTok   = Get-Token 'analyst-alice' @('risk:score','risk:investigate')
$approveTok  = Get-Token 'approver-bob'   @('risk:score','risk:investigate','risk:approve')
Write-Host "  scoreToken   -> $($scoreTok.Substring(0,24))..."
Write-Host "  investigator -> $($investTok.Substring(0,24))..."
Write-Host "  approver     -> $($approveTok.Substring(0,24))..."

# ---------------------------------------------------------------------------
# 2. Score a small synthetic stream
# ---------------------------------------------------------------------------
Write-Section "2. Scoring $Count synthetic transactions"
$rand   = [System.Random]::new(42)
$mccs   = @('5411','5732','5812','5967','6011','7995')
$countries = @('KE','US','NG','GB')
$score200 = 0; $scoreOther = 0; $totalLatency = 0.0
$refs = New-Object System.Collections.Generic.List[string]
$decisionMap = @{}

for ($i = 0; $i -lt $Count; $i++) {
    $ref  = "demo-$([Guid]::NewGuid().ToString('N').Substring(0,10))"
    $card = "card-demo-$($rand.Next(1,6))"
    $body = @{
        transactionRef = $ref
        cardId         = $card
        customerId     = "cust-$($rand.Next(1,6))"
        deviceId       = "device-$($rand.Next(1,4))"
        ipAddress      = "10.0.$($rand.Next(0,255)).$($rand.Next(1,254))"
        merchantId     = "merch-$($rand.Next(1,10))"
        mcc            = $mccs[$rand.Next(0,$mccs.Count)]
        amount         = [Math]::Round([decimal]$rand.NextDouble() * 500, 2)
        currency       = 'USD'
        type           = 'CardNotPresent'
        latitude       = -1.28 + [double]$rand.NextDouble()
        longitude      = 36.82 + [double]$rand.NextDouble()
        country        = $countries[$rand.Next(0,$countries.Count)]
        occurredAt     = ([DateTimeOffset]::UtcNow).ToString('o')
    } | ConvertTo-Json -Compress
    try {
        $res = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/transactions/score" `
            -Headers @{ Authorization = "Bearer $scoreTok" } -ContentType 'application/json' -Body $body
        $refs.Add($ref)
        $decisionMap[$res.decision] = ($decisionMap[$res.decision] ?? 0) + 1
        $totalLatency += [double]$res.latencyMs
        $score200++
    } catch {
        $scoreOther++
    }
}
Write-Host ("  scored ok:      $score200/$Count") -ForegroundColor Green
Write-Host ("  scored failed:  $scoreOther")     -ForegroundColor Yellow
if ($score200 -gt 0) {
    Write-Host ("  avg latency:    {0:N2} ms" -f ($totalLatency / $score200))
    foreach ($k in $decisionMap.Keys) { Write-Host ("  decisions:      $k = $($decisionMap[$k])") }
}

# ---------------------------------------------------------------------------
# 3. List alerts
# ---------------------------------------------------------------------------
Write-Section '3. Listing alerts (top 10)'
$alerts = Invoke-Auth 'Get' '/api/v1/alerts?page=1&pageSize=10' $investTok $null
$alertCount = if ($null -ne $alerts.items) { $alerts.items.Count } elseif ($alerts -is [array]) { $alerts.Count } else { 0 }
Write-Host "  alert count in first page: $alertCount"

# ---------------------------------------------------------------------------
# 4. List cases and pick one to work
# ---------------------------------------------------------------------------
Write-Section '4. Listing cases'
$cases = Invoke-Auth 'Get' '/api/v1/cases?page=1&pageSize=10' $investTok $null
$caseList = if ($null -ne $cases.items) { $cases.items } else { $cases }
if (-not $caseList -or $caseList.Count -eq 0) {
    Write-Host '  no cases yet in this run — increase -Count and try again' -ForegroundColor Yellow
} else {
    $case = $caseList[0]
    Write-Host "  working case: $($case.id)"

    Write-Section '5. Assign, note, propose disposition, approve'
    Invoke-Auth 'Post' "/api/v1/cases/$($case.id)/assign" $investTok @{ investigator = 'analyst-alice' }         | Out-Null
    Invoke-Auth 'Post' "/api/v1/cases/$($case.id)/notes"  $investTok @{ author = 'analyst-alice'; text = 'reviewed velocity + IP mismatch' } | Out-Null
    $prop = Invoke-Auth 'Post' "/api/v1/cases/$($case.id)/disposition" $investTok @{ disposition = 'ConfirmedFraud'; reason = 'Confirmed card-testing pattern'; by = 'analyst-alice' }
    if ($prop.status -eq 'AwaitingApproval' -or $prop.awaitingApproval -eq $true) {
        Write-Host '  case is AwaitingApproval (four-eyes threshold triggered)'
        Invoke-Auth 'Post' "/api/v1/cases/$($case.id)/approve" $approveTok @{ approver = 'approver-bob' } | Out-Null
        Write-Host '  approver-bob approved -> case disposed'
    } else {
        Write-Host '  case disposed without four-eyes (exposure below threshold)'
    }
}

# ---------------------------------------------------------------------------
# 6. Tuning report / detection metrics
# ---------------------------------------------------------------------------
Write-Section '6. Fetching detection metrics (labelled window)'
try {
    $metrics = Invoke-Auth 'Get' '/api/v1/metrics/detection' $investTok $null
    Write-Host ("  precision: {0}  recall: {1}  fpr: {2}  alerts: {3}  valueDetected: {4}" -f `
        $metrics.precision, $metrics.recall, $metrics.falsePositiveRate, $metrics.alerts, $metrics.valueDetected)
} catch {
    Write-Host "  metrics call failed: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Section 'Demo complete'
Write-Host 'See docs/detection-performance.md for full measured numbers.' -ForegroundColor Green
