<#
  demo.ps1 — end-to-end demonstration of the AI Agent Workflow Orchestration Platform.

  Starts the API on http://localhost:5029, then drives all THREE seeded workflows over REST,
  including a human approval, a blocked prompt-injection attempt, and the offline evaluation gate.
  Everything runs offline against the DeterministicMockModel — no API keys, no network.

  Usage:   pwsh -File scripts/demo.ps1
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$baseUrl = 'http://localhost:5029'

function Write-Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }

Write-Section 'Starting the API (Release)'
$api = Start-Process dotnet `
    -ArgumentList 'run','--project','src/AgentPlatform.Api','-c','Release','--launch-profile','http' `
    -WorkingDirectory $root -PassThru

try {
    Write-Host 'Waiting for /health ...' -NoNewline
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $h = Invoke-RestMethod "$baseUrl/health" -TimeoutSec 2
            if ($h) { $ready = $true; break }
        } catch { Start-Sleep -Seconds 1; Write-Host '.' -NoNewline }
    }
    if (-not $ready) { throw 'API did not become healthy in time.' }
    Write-Host ' ready.' -ForegroundColor Green

    # --- Auth: mint a dev token with all scopes ------------------------------
    Write-Section 'Minting a dev token'
    $tok = Invoke-RestMethod "$baseUrl/api/v1/dev/token" -Method Post -ContentType 'application/json' `
        -Body (@{ subject = 'demo'; tenant = 'tenant-alpha'; scopes = @('agents:run','agents:approve','agents:admin') } | ConvertTo-Json)
    $headers = @{ Authorization = "Bearer $($tok.access_token)" }
    Write-Host "token acquired (scopes: $($tok.scopes -join ', '))"

    function Start-Run($workflow, $inputs) {
        Invoke-RestMethod "$baseUrl/api/v1/runs" -Method Post -Headers $headers -ContentType 'application/json' `
            -Body (@{ workflowName = $workflow; inputs = $inputs } | ConvertTo-Json -Depth 8)
    }
    function Get-Run($id) { Invoke-RestMethod "$baseUrl/api/v1/runs/$id" -Headers $headers }

    # --- Workflow 1: support-ticket triage -----------------------------------
    Write-Section 'Workflow 1: support-ticket triage'
    $r = Start-Run 'support-ticket-triage' @{ ticket_id = 'TCK-1001' }
    Write-Host "  TCK-1001 (low-risk)  -> $($r.status)/$($r.outcome)  [auto-resolved]"
    $r = Start-Run 'support-ticket-triage' @{ ticket_id = 'TCK-1016' }
    Write-Host "  TCK-1016 (sensitive) -> $($r.status)/$($r.outcome)  [escalated to a human]"

    Write-Host '  Prompt-injection ticket (adversarial):' -ForegroundColor Yellow
    $inj = Start-Run 'support-ticket-triage' @{ ticket_id = 'TCK-INJ-1' }
    Write-Host "  TCK-INJ-1 -> $($inj.status)/$($inj.outcome)  [injected send_email call BLOCKED by the step allow-list]"
    $trace = Invoke-RestMethod "$baseUrl/api/v1/runs/$($inj.id)/trace" -Headers $headers
    $blocked = $trace.events | Where-Object { $_.tool -eq 'send_email' -and -not $_.success }
    if ($blocked) { Write-Host "  Trace records the blocked tool call as a policy violation." -ForegroundColor Green }

    # --- Workflow 2: document summarisation & extraction ---------------------
    Write-Section 'Workflow 2: document summarisation & extraction'
    $doc = 'INVOICE 2026-014. Amount due USD 1,240.00 for consulting services rendered to Contoso Retail (fictional). ' +
           'Net 30 terms. This document is long enough for the deterministic extractor to report high confidence, so the ' +
           'workflow validates the extracted fields and completes without human review.'
    $r = Start-Run 'document-summarisation-extraction' @{ document = $doc }
    Write-Host "  clean document      -> $($r.status)/$($r.outcome)  [validated, no review needed]"
    $r = Start-Run 'document-summarisation-extraction' @{ document = 'Too short to be confident.' }
    Write-Host "  low-confidence doc  -> $($r.status)/$($r.outcome)  [flagged for human review]"

    # --- Workflow 3: refund approval (human-in-the-loop) ---------------------
    Write-Section 'Workflow 3: refund approval (deterministic eligibility + human approval)'
    $refund = Start-Run 'refund-approval' @{
        customer_id = 'CUST-001'; order_amount = 50; currency = 'USD'
        days_since_purchase = 10; reason_category = 'change_of_mind'; item_returned = $true
    }
    Write-Host "  refund run started   -> $($refund.status)  [eligibility computed in CODE, not by the model]"

    $pending = Invoke-RestMethod "$baseUrl/api/v1/approvals" -Headers $headers
    $approval = $pending | Where-Object { $_.runId -eq $refund.id } | Select-Object -First 1
    Write-Host "  approval task raised -> '$($approval.title)' (risk: $($approval.riskLevel))"

    Invoke-RestMethod "$baseUrl/api/v1/approvals/$($approval.id)/approve" -Method Post -Headers $headers `
        -ContentType 'application/json' -Body (@{ notes = 'Approved in demo' } | ConvertTo-Json) | Out-Null
    $done = Get-Run $refund.id
    Write-Host "  after approval       -> $($done.status)/$($done.outcome)  [refund executed idempotently + audited]" -ForegroundColor Green

    # --- Offline evaluation + regression gate --------------------------------
    Write-Section 'Offline evaluation harness + regression gate'
    $cmp = Invoke-RestMethod "$baseUrl/api/v1/evals/compare" -Method Post -Headers $headers `
        -ContentType 'application/json' -Body (@{ } | ConvertTo-Json)
    $rep = $cmp.report
    Write-Host ("  scenarios: {0}/{1} passed  |  task success: {2:P0}  |  unauthorised handled: {3:P0}" -f `
        $rep.totalPassed, $rep.totalScenarios, $rep.overallTaskSuccess, $rep.overallUnauthorisedHandling)
    Write-Host ("  regression gate: {0}" -f ($(if ($cmp.gate.passed) { 'PASS' } else { 'FAIL' }))) -ForegroundColor Green

    Write-Section 'Demo complete'
    Write-Host "Open $baseUrl/ in a browser for the console (runs, traces, approvals, tools, evals)."
}
finally {
    if ($api -and -not $api.HasExited) {
        Write-Host "`nStopping the API (PID $($api.Id)) ..." -ForegroundColor DarkGray
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
    }
}
