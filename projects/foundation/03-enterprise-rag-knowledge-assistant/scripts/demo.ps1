#requires -Version 7.0
<#
.SYNOPSIS
    Five-minute demo of the Enterprise RAG Knowledge Assistant.
.DESCRIPTION
    Boots the API in the background, hits every important endpoint with two
    identities (employee vs. board), and prints the answers side by side so
    the reviewer can verify the permission-aware behaviour, grounded
    citations and refusal handling — all offline.

    Usage: ./scripts/demo.ps1
    Stop:  Ctrl+C. The trap block kills the API process on exit.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot 'src\RagAssistant.Api'
$baseUrl = 'http://localhost:5003'

function Wait-Ready {
    param([int]$TimeoutSec = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "$baseUrl/health/ready" -UseBasicParsing -TimeoutSec 2 -ErrorAction SilentlyContinue
            if ($r.StatusCode -eq 200) { return }
        }
        catch {}
        Start-Sleep -Milliseconds 500
    }
    throw "API did not become ready within $TimeoutSec seconds."
}

function Invoke-Json {
    param(
        [string]$Method,
        [string]$Path,
        [string]$Token,
        $Body
    )
    $params = @{
        Method  = $Method
        Uri     = "$baseUrl$Path"
        Headers = @{ 'Content-Type' = 'application/json' }
    }
    if ($Token) { $params.Headers.Authorization = "Bearer $Token" }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 6)
    }
    $response = Invoke-WebRequest @params -UseBasicParsing
    return $response.Content | ConvertFrom-Json
}

Push-Location $repoRoot
try {
    Write-Host "== Building solution ==" -ForegroundColor Cyan
    dotnet build -c Release --nologo | Out-Null

    Write-Host "== Starting API on $baseUrl ==" -ForegroundColor Cyan
    $api = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '-c', 'Release', '--project', $apiProject, '--no-launch-profile', '--no-build') -PassThru -WindowStyle Hidden

    try {
        Wait-Ready

        Write-Host "== Issue tokens (dev endpoint) ==" -ForegroundColor Cyan
        $employee = (Invoke-Json -Method POST -Path '/api/v1/auth/token' -Body @{ userId = 'alice';     roles = @('employee'); departments = @('hr'); classification = 'Internal' }).token
        $board    = (Invoke-Json -Method POST -Path '/api/v1/auth/token' -Body @{ userId = 'boardroom'; roles = @('employee','board','admin'); departments = @(); classification = 'Restricted' }).token

        Write-Host "== 1. Grounded answer (employee) ==" -ForegroundColor Green
        Invoke-Json -Method POST -Path '/api/v1/query' -Token $employee -Body @{ query = 'How many paid time off days do employees receive?'; mode = 'Hybrid'; topK = 4 } | ConvertTo-Json -Depth 6

        Write-Host "`n== 2. Refusal below threshold ==" -ForegroundColor Green
        Invoke-Json -Method POST -Path '/api/v1/query' -Token $employee -Body @{ query = 'What is the capital of Mars?'; mode = 'Hybrid'; topK = 4 } | ConvertTo-Json -Depth 6

        Write-Host "`n== 3. Restricted question, unauthorised user ==" -ForegroundColor Green
        $restrictedAnswer = Invoke-Json -Method POST -Path '/api/v1/query' -Token $employee -Body @{ query = 'What is the CEO LTIP pool for the current fiscal year?'; mode = 'Hybrid'; topK = 4 }
        $restrictedAnswer | ConvertTo-Json -Depth 6
        if ($restrictedAnswer.answer -match 'LTIP|3\.5\s*million') { throw 'LEAK: restricted content reached an employee!' }

        Write-Host "`n== 4. Board user can see the restricted document ==" -ForegroundColor Green
        $docs = Invoke-Json -Method GET -Path '/api/v1/documents?pageSize=100' -Token $board
        $docs | Where-Object { $_.classification -eq 'Restricted' } | ConvertTo-Json -Depth 6

        Write-Host "`n== 5. Evaluation harness ==" -ForegroundColor Green
        Invoke-Json -Method POST -Path '/api/v1/eval/runs' -Token $board -Body @{} | ConvertTo-Json -Depth 6
    }
    finally {
        Write-Host "`n== Stopping API ==" -ForegroundColor Cyan
        if ($api -and -not $api.HasExited) {
            Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    Pop-Location
}
