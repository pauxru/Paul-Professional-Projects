[CmdletBinding()]
param(
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Iiot.Api\Iiot.Api.csproj'

Write-Host 'Starting API, embedded TCP MQTT broker, simulated devices, and edge gateway...'
$process = Start-Process -FilePath dotnet `
    -ArgumentList @('run', '--project', $project, '--launch-profile', 'Iiot.Api') `
    -WorkingDirectory $root `
    -PassThru

try {
    $ready = $false
    for ($attempt = 1; $attempt -le 30 -and -not $ready; $attempt++) {
        Start-Sleep -Seconds 1
        try {
            Invoke-RestMethod 'http://localhost:5010/health/ready' | Out-Null
            $ready = $true
        }
        catch {
            if ($process.HasExited) {
                throw "API process exited with code $($process.ExitCode)."
            }
        }
    }
    if (-not $ready) {
        throw 'API did not become ready within 30 seconds.'
    }

    $token = (Invoke-RestMethod 'http://localhost:5010/api/v1/auth/token' -Method Post `
        -ContentType 'application/json' `
        -Body '{"subject":"demo-script","scope":"admin operator"}').accessToken
    $headers = @{ Authorization = "Bearer $token" }

    Write-Host 'Dashboard: http://localhost:5010/'
    Write-Host 'Embedded MQTT broker: tcp://localhost:18830'
    Write-Host 'Cutting cloud link for 12 seconds; devices and local edge rules keep running...'
    Invoke-RestMethod 'http://localhost:5010/api/v1/demo/network' -Method Post -Headers $headers `
        -ContentType 'application/json' -Body '{"online":false}' | Format-List
    Start-Sleep -Seconds 12

    Write-Host 'Restoring cloud link and allowing ordered replay...'
    Invoke-RestMethod 'http://localhost:5010/api/v1/demo/network' -Method Post -Headers $headers `
        -ContentType 'application/json' -Body '{"online":true}' | Format-List
    Start-Sleep -Seconds 5
    Write-Host 'Gateway state after replay:'
    Invoke-RestMethod 'http://localhost:5010/api/v1/demo/network' -Headers $headers | Format-List
    Invoke-RestMethod 'http://localhost:5010/api/v1/devices' -Headers $headers | Format-Table deviceId, status, firmwareVersion
    Write-Host 'Inspect docs\edge-buffering-test.md for the verified deterministic replay invariant.'

    if ($KeepRunning) {
        Write-Host 'Press Ctrl+C to stop the demo.'
        Wait-Process -Id $process.Id
    }
}
finally {
    if (-not $KeepRunning -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
        Write-Host 'Stopped demo API process.'
    }
}
