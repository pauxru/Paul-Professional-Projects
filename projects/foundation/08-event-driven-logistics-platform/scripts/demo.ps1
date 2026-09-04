param(
    [int]$VehicleCount = 3,
    [int]$PingsPerVehicle = 120,
    [int]$WatchSeconds = 20
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $root 'src\SavannaLogistics.Api'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5008'
$process = Start-Process dotnet -ArgumentList @('run', '--project', $apiProject, '--no-launch-profile') `
    -WorkingDirectory $root -PassThru `
    -RedirectStandardOutput (Join-Path $root 'demo-api.stdout.log') `
    -RedirectStandardError (Join-Path $root 'demo-api.stderr.log')

try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            Invoke-RestMethod -Uri 'http://localhost:5008/health/ready' | Out-Null
            $ready = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) { throw 'API did not become ready.' }

    $token = Invoke-RestMethod -Method Post -Uri 'http://localhost:5008/api/v1/auth/token' `
        -ContentType 'application/json' -Body '{"clientId":"operator"}'
    $headers = @{ Authorization = "Bearer $($token.accessToken)" }

    $simulatorBody = @{
        vehicleCount = $VehicleCount
        pingsPerVehicle = $PingsPerVehicle
        pingsPerSecond = 30
        seed = 808
        duplicateRate = 0.05
        outOfOrderRate = 0.10
        dropoutRate = 0.06
        gpsJitterMetres = 15
        clockSkewSeconds = 5
        realTime = $false
    } | ConvertTo-Json
    Write-Host 'Running fault-injecting simulator...'
    Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' `
        -Uri 'http://localhost:5008/api/v1/telemetry/simulate' -Body $simulatorBody |
        ConvertTo-Json -Depth 6

    Write-Host "Watching alerts for $WatchSeconds seconds..."
    $until = (Get-Date).AddSeconds($WatchSeconds)
    while ((Get-Date) -lt $until) {
        $alerts = Invoke-RestMethod -Headers $headers `
            -Uri 'http://localhost:5008/api/v1/alerts?page=1&pageSize=10'
        Clear-Host
        $alerts.items | Select-Object occurredAt, type, vehicleId, message | Format-Table -AutoSize
        Start-Sleep -Seconds 2
    }

    $to = [DateTimeOffset]::UtcNow
    $from = $to.AddHours(-1)
    $replayBody = @{ from = $from; to = $to; speed = 0; vehicleId = $null } | ConvertTo-Json
    Write-Host 'Replaying the last hour at maximum speed (duplicate-alert safe)...'
    Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' `
        -Uri 'http://localhost:5008/api/v1/replay' -Body $replayBody |
        ConvertTo-Json -Depth 5
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
        $process.WaitForExit()
    }
}
