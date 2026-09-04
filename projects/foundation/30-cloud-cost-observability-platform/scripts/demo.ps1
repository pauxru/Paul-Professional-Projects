[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host '1/6 Building local FinOps platform...'
dotnet build -c Release --nologo

Write-Host '2/6 Starting API; Development startup generates and imports deterministic synthetic data...'
$api = Start-Process dotnet -ArgumentList 'run', '--project', 'src\CloudCostObservability.Api', '-c', 'Release', '--no-build' -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 300 -and -not $ready; $attempt++) {
        Start-Sleep -Seconds 1
        try {
            $ready = (Invoke-WebRequest 'http://localhost:5030/health/ready' -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200
        } catch { }
    }
    if (-not $ready) { throw 'API did not become ready within 300 seconds.' }

    $token = (Invoke-RestMethod 'http://localhost:5030/api/v1/auth/token' -Method Post -ContentType 'application/json' -Body '{"subject":"demo-admin","scope":"finops:read finops:manage finops:admin"}').accessToken
    $headers = @{ Authorization = "Bearer $token" }

    Write-Host '3/6 Import completed during idempotent Development seed:'
    Invoke-RestMethod 'http://localhost:5030/api/v1/imports' -Headers $headers | ConvertTo-Json -Depth 4

    Write-Host '4/6 Allocating and reconciling the last month:'
    Invoke-RestMethod 'http://localhost:5030/api/v1/allocations?from=2026-08-01&to=2026-08-31&pageSize=5' -Headers $headers | ConvertTo-Json -Depth 5

    Write-Host '5/6 Detected anomalies and generated recommendations:'
    Invoke-RestMethod 'http://localhost:5030/api/v1/anomalies?includeSuppressed=true' -Headers $headers | Select-Object -First 5 | ConvertTo-Json -Depth 4
    Invoke-RestMethod 'http://localhost:5030/api/v1/recommendations' -Headers $headers | Select-Object -First 5 | ConvertTo-Json -Depth 4

    Write-Host '6/6 Forecasting with measured method comparison:'
    Invoke-RestMethod 'http://localhost:5030/api/v1/forecasts' -Headers $headers | ConvertTo-Json -Depth 4
    Write-Host 'Dashboard: http://localhost:5030'
} finally {
    if (-not $api.HasExited) { Stop-Process -Id $api.Id }
}
