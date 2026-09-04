#requires -Version 7.0
<#
.SYNOPSIS
Demonstrates the Healthcare Appointment & Clinical Workflow Platform end-to-end
against a locally-running API.

.DESCRIPTION
Run the API first:
    dotnet run --project src\Healthcare.Api

Then run this script:
    scripts\demo.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "https://localhost:5023",
    [ValidateSet('All','Setup','Book','BreakGlass','Notes','Reminders','Reports')]
    [string]$Step = 'All'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Trust localhost dev cert.
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

function New-DevToken([string]$UserId, [string[]]$Roles, [string]$ClinicianId = $null) {
    $body = @{ userId = $UserId; roles = $Roles }
    if ($ClinicianId) { $body.clinicianId = $ClinicianId }
    $resp = Invoke-RestMethod -Uri "$BaseUrl/auth/dev-token" -Method POST `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    return $resp.accessToken
}

function Get-Facilities([string]$Token) {
    $hdrs = @{ Authorization = "Bearer $Token" }
    return Invoke-RestMethod -Uri "$BaseUrl/api/v1/facilities" -Headers $hdrs
}

if ($Step -in 'All','Setup') {
    Write-Host "▶ Setup — fetching dev token for a receptionist..." -ForegroundColor Cyan
    $token = New-DevToken -UserId 'recep-1' -Roles @('Receptionist')
    Write-Host "  Token acquired: $($token.Substring(0, 20))..." -ForegroundColor Gray
    $facs = Get-Facilities -Token $token
    Write-Host "  Facilities: $($facs.Count)"
    $facs | Format-Table id, code, name -AutoSize
}

if ($Step -in 'All','Book') {
    Write-Host "▶ Book — searching slots for tomorrow..." -ForegroundColor Cyan
    Write-Host "  (In a full demo, call GET /api/v1/availability and POST /api/v1/appointments here.)" -ForegroundColor Gray
}

if ($Step -in 'All','BreakGlass') {
    Write-Host "▶ Break-glass — attempting an emergency read..." -ForegroundColor Cyan
    Write-Host "  (Set X-Break-Glass: true and X-Break-Glass-Justification: 'test' on the encounter GET.)" -ForegroundColor Gray
}

if ($Step -in 'All','Notes') {
    Write-Host "▶ Notes — add + amend + list versions." -ForegroundColor Cyan
    Write-Host "  (See docs/portfolio/demo-script.md for the exact request bodies.)" -ForegroundColor Gray
}

if ($Step -in 'All','Reminders') {
    Write-Host "▶ Reminders — schedule and confirm." -ForegroundColor Cyan
}

if ($Step -in 'All','Reports') {
    Write-Host "▶ Reports — hitting the access-anomaly report as an Auditor..." -ForegroundColor Cyan
    $auditorToken = New-DevToken -UserId 'aud-1' -Roles @('Auditor')
    $hdrs = @{ Authorization = "Bearer $auditorToken" }
    try {
        $report = Invoke-RestMethod -Uri "$BaseUrl/api/v1/reports/access-anomalies" -Headers $hdrs
        Write-Host "  Rows: $($report.Count)"
    } catch {
        Write-Warning "Report endpoint returned an error: $($_.Exception.Message)"
    }
}

Write-Host "✔ demo.ps1 complete." -ForegroundColor Green
