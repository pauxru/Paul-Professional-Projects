$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Building Integration Hub..." -ForegroundColor Cyan
dotnet build IntegrationHub.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$logDir = Join-Path $root ".demo"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$processes = @()

function Start-DemoHost([string]$name, [string]$project, [int]$port) {
    $stdout = Join-Path $logDir "$name.out.log"
    $stderr = Join-Path $logDir "$name.err.log"
    $process = Start-Process dotnet `
        -ArgumentList @("run", "--project", $project, "-c", "Release", "--no-build", "--urls", "http://localhost:$port") `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $script:processes += $process
}

function Wait-Ready([string]$url) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Invoke-WebRequest -Uri $url -UseBasicParsing | Out-Null
            return
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Host did not become ready: $url"
}

try {
    Start-DemoHost "crm" "src\IntegrationHub.Simulators.Crm" 5112
    Start-DemoHost "erp" "src\IntegrationHub.Simulators.Erp" 5212
    Start-DemoHost "payments" "src\IntegrationHub.Simulators.Payments" 5312
    Start-DemoHost "hub" "src\IntegrationHub.Api" 5012

    Wait-Ready "http://localhost:5112/health"
    Wait-Ready "http://localhost:5212/health"
    Wait-Ready "http://localhost:5312/health"
    Wait-Ready "http://localhost:5012/health/ready"

    $token = Invoke-RestMethod -Method Post -Uri "http://localhost:5012/api/v1/auth/token" `
        -ContentType "application/json" `
        -Body (@{
            clientId = "demo-client"
            clientSecret = "dev-only-client-secret"
            scopes = @("hub.read", "hub.write", "hub.admin")
        } | ConvertTo-Json)
    $hubHeaders = @{ Authorization = "Bearer $($token.access_token)" }
    $basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("demo:dev-only-simulator-password"))
    $erpHeaders = @{ Authorization = "Basic $basic" }

    $flows = Invoke-RestMethod -Uri "http://localhost:5012/api/v1/flows" -Headers $hubHeaders
    $flow = $flows | Where-Object name -eq "CRM contacts to ERP customers" | Select-Object -First 1
    if (-not $flow) { throw "Seeded CRM to ERP flow was not found." }

    Write-Host "Forcing one ERP validation failure..." -ForegroundColor Yellow
    Invoke-RestMethod -Method Post -Uri "http://localhost:5212/admin/faults" `
        -Headers $erpHeaders -ContentType "application/json" `
        -Body '{"failNext":0,"throttleNext":0,"rejectNext":1,"retryAfterSeconds":1}' | Out-Null

    $run = Invoke-RestMethod -Method Post -Uri "http://localhost:5012/api/v1/flows/$($flow.id)/runs" `
        -Headers $hubHeaders -ContentType "application/json" -Body '{"payload":{}}'
    Write-Host "Run $($run.id) finished with status $($run.status)." -ForegroundColor Cyan

    $deadLetters = Invoke-RestMethod -Uri "http://localhost:5012/api/v1/dead-letters?status=Pending" -Headers $hubHeaders
    $item = $deadLetters | Select-Object -First 1
    if (-not $item) { throw "Expected a quarantined record." }
    Write-Host "Replaying DLQ item $($item.id) with its original idempotency key..." -ForegroundColor Yellow
    $replay = Invoke-RestMethod -Method Post -Uri "http://localhost:5012/api/v1/dead-letters/replay" `
        -Headers $hubHeaders -ContentType "application/json" `
        -Body (@{ itemId = $item.id } | ConvertTo-Json)

    Write-Host "Replay $($replay.id) status: $($replay.status)." -ForegroundColor Green
    Write-Host "Admin UI: http://localhost:5012" -ForegroundColor Green
    Write-Host "Logs: $logDir"
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id
        }
    }
}
