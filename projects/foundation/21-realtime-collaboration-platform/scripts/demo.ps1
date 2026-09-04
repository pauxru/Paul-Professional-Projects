#requires -Version 7
<#
.SYNOPSIS
    End-to-end demo for Collab (Project 21 — Real-Time Collaboration Backend).

.DESCRIPTION
    Starts the API (unless -NoServe), waits for health, then drives the REST surface with two
    seeded users (Ada = Owner, Grace = Editor) against the seeded demo workspace and runbook
    document. Finally prints the URL to open two browser tabs for live co-editing.

    No external infrastructure required (SQLite + SignalR over the in-proc host).

.EXAMPLE
    ./scripts/demo.ps1
.EXAMPLE
    ./scripts/demo.ps1 -NoServe        # if the API is already running on :5021
#>
param(
    [switch]$NoServe,
    [string]$BaseUrl = "http://localhost:5021"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$runbookId = "55555555-5555-5555-5555-555555555555"   # seeded Text document
$serverProc = $null

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

function Get-Token($email) {
    $body = @{ email = $email } | ConvertTo-Json
    $res = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/token" -ContentType "application/json" -Body $body
    return $res
}

function Auth($token) { @{ Authorization = "Bearer $token" } }

try {
    if (-not $NoServe) {
        Write-Step "Starting API (Release) on $BaseUrl"
        $serverProc = Start-Process -FilePath "dotnet" `
            -ArgumentList @("run","-c","Release","--project","$root\src\Collab.Api\Collab.Api.csproj") `
            -PassThru -WindowStyle Hidden

        Write-Host "Waiting for /health ..." -NoNewline
        $ready = $false
        for ($i = 0; $i -lt 60; $i++) {
            try {
                $h = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 2
                if ($h.status -eq "healthy") { $ready = $true; break }
            } catch { Start-Sleep -Milliseconds 500; Write-Host "." -NoNewline }
        }
        Write-Host ""
        if (-not $ready) { throw "API did not become healthy in time." }
    }

    Write-Step "Sign in (password-less demo tokens)"
    $ada   = Get-Token "ada@acme.example"
    $grace = Get-Token "grace@acme.example"
    Write-Host "Ada   -> $($ada.userId)  ($($ada.displayName))"
    Write-Host "Grace -> $($grace.userId)  ($($grace.displayName))"

    Write-Step "Workspaces visible to Ada (Owner)"
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/workspaces" -Headers (Auth $ada.token) |
        ConvertTo-Json -Depth 6

    $wsId = (Invoke-RestMethod -Uri "$BaseUrl/api/v1/workspaces" -Headers (Auth $ada.token))[0].id

    Write-Step "Documents in the workspace"
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/documents?workspaceId=$wsId" -Headers (Auth $ada.token) |
        ConvertTo-Json -Depth 6

    Write-Step "Runbook content + history (first page)"
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/documents/$runbookId" -Headers (Auth $ada.token) |
        ConvertTo-Json -Depth 6
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/documents/$runbookId/history" -Headers (Auth $ada.token) |
        ConvertTo-Json -Depth 6

    Write-Step "Ada comments on the runbook and @mentions Grace"
    $comment = @{ body = "Please review step 2 @grace"; anchorStart = 0; anchorEnd = 3; mentions = @($grace.userId) } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/documents/$runbookId/comments" `
        -Headers (Auth $ada.token) -ContentType "application/json" -Body $comment | ConvertTo-Json -Depth 6

    Write-Step "Grace's notification inbox (should include the mention)"
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/notifications" -Headers (Auth $grace.token) | ConvertTo-Json -Depth 6

    Write-Step "LIVE CO-EDITING"
    Write-Host "Open TWO browser tabs at: $BaseUrl/" -ForegroundColor Green
    Write-Host "  Tab 1: sign in as ada@acme.example   -> Connect & join (doc $runbookId)"
    Write-Host "  Tab 2: sign in as grace@acme.example -> Connect & join (same doc)"
    Write-Host "Type in either tab; edits and cursors converge in real time." -ForegroundColor Green

    if (-not $NoServe) {
        Write-Host "`nPress Enter to stop the API..." -ForegroundColor Yellow
        [void][System.Console]::ReadLine()
    }
}
finally {
    if ($serverProc -and -not $serverProc.HasExited) {
        Write-Step "Stopping API (PID $($serverProc.Id))"
        Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue
    }
}
