[CmdletBinding()]
param(
    [switch] $InjectGreenFailure,
    [int] $RequestsPerStep = 40,
    [double] $MaximumErrorRate = 0.02,
    [double] $MaximumP95LatencyMs = 500
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CanaryGate.psm1') -Force

$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts\canary'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$blueDb = Join-Path $artifacts 'blue.db'
$greenDb = Join-Path $artifacts 'green.db'

function Invoke-Migrations([string] $databasePath) {
    $previous = $env:Database__ConnectionString
    $previousLogLevel = $env:Logging__LogLevel__Default
    try {
        $env:Database__ConnectionString = "Data Source=$databasePath"
        $env:Logging__LogLevel__Default = 'Warning'
        & dotnet run --project (Join-Path $root 'src\Contoso.Storefront.MigrationRunner') -c Release --no-build
        if ($LASTEXITCODE -ne 0) {
            throw "Migration runner failed for $databasePath."
        }
    }
    finally {
        $env:Database__ConnectionString = $previous
        $env:Logging__LogLevel__Default = $previousLogLevel
    }
}

function Wait-Ready([string] $baseUrl) {
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        try {
            $result = Invoke-RestMethod "$baseUrl/health/ready" -TimeoutSec 2
            if ($result.status -eq 'Healthy') {
                return
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 250
    }
    throw "$baseUrl did not become ready."
}

dotnet build (Join-Path $root 'Contoso.Storefront.slnx') -c Release --nologo --verbosity quiet | Out-Host
Invoke-Migrations $blueDb
Invoke-Migrations $greenDb

$blueLog = Join-Path $artifacts 'blue.log'
$blueErrorLog = Join-Path $artifacts 'blue.err.log'
$greenLog = Join-Path $artifacts 'green.log'
$greenErrorLog = Join-Path $artifacts 'green.err.log'
$commonEnvironment = @{
    ASPNETCORE_ENVIRONMENT           = 'Development'
    Observability__Exporter          = 'None'
    Operations__PreStopDelaySeconds  = '0'
    Operations__WorkerPollIntervalMs = '20'
}

$blueEnvironment = $commonEnvironment.Clone()
$blueEnvironment['ASPNETCORE_URLS'] = 'http://localhost:5028'
$blueEnvironment['Database__ConnectionString'] = "Data Source=$blueDb"
$blueEnvironment['Downstream__BaseUrl'] = 'http://localhost:5028/simulated/downstream'
$blueEnvironment['Features__NewPricingDarkLaunch'] = 'false'

$greenEnvironment = $commonEnvironment.Clone()
$greenEnvironment['ASPNETCORE_URLS'] = 'http://localhost:5128'
$greenEnvironment['Database__ConnectionString'] = "Data Source=$greenDb"
$greenEnvironment['Downstream__BaseUrl'] = 'http://localhost:5128/simulated/downstream'
$greenEnvironment['Features__NewPricingDarkLaunch'] = 'true'

$blue = Start-Process dotnet `
    -ArgumentList @('run', '--project', (Join-Path $root 'src\Contoso.Storefront.Api'), '-c', 'Release', '--no-build', '--no-launch-profile') `
    -Environment $blueEnvironment `
    -RedirectStandardOutput $blueLog `
    -RedirectStandardError $blueErrorLog `
    -PassThru
$green = Start-Process dotnet `
    -ArgumentList @('run', '--project', (Join-Path $root 'src\Contoso.Storefront.Api'), '-c', 'Release', '--no-build', '--no-launch-profile') `
    -Environment $greenEnvironment `
    -RedirectStandardOutput $greenLog `
    -RedirectStandardError $greenErrorLog `
    -PassThru

try {
    Wait-Ready 'http://localhost:5028'
    Wait-Ready 'http://localhost:5128'

    foreach ($greenWeight in @(5, 20, 50, 100)) {
        $statuses = [Collections.Generic.List[int]]::new()
        $latencies = [Collections.Generic.List[double]]::new()

        for ($request = 0; $request -lt $RequestsPerStep; $request++) {
            $toGreen = (Get-Random -Minimum 1 -Maximum 101) -le $greenWeight
            $baseUrl = $toGreen ? 'http://localhost:5128' : 'http://localhost:5028'
            $fail = $InjectGreenFailure -and $toGreen
            $watch = [Diagnostics.Stopwatch]::StartNew()
            try {
                $response = Invoke-WebRequest "$baseUrl/simulated/downstream?fail=$($fail.ToString().ToLowerInvariant())" -SkipHttpErrorCheck -TimeoutSec 5
                $statuses.Add([int]$response.StatusCode)
            }
            catch {
                $statuses.Add(599)
            }
            finally {
                $watch.Stop()
                $latencies.Add($watch.Elapsed.TotalMilliseconds)
            }
        }

        Invoke-RestMethod 'http://localhost:5028/health/ready' | Out-Null
        Invoke-RestMethod 'http://localhost:5128/health/ready' | Out-Null
        Invoke-RestMethod 'http://localhost:5028/metrics' | Out-Null
        Invoke-RestMethod 'http://localhost:5128/metrics' | Out-Null

        $metrics = New-CanaryMetrics -StatusCodes $statuses.ToArray() -LatenciesMs $latencies.ToArray()
        $gate = Test-CanaryGate `
            -Metrics $metrics `
            -MaximumErrorRate $MaximumErrorRate `
            -MaximumP95LatencyMs $MaximumP95LatencyMs `
            -MinimumSampleCount $RequestsPerStep

        Write-Host "Green weight $greenWeight%: $($gate.Decision) — $($gate.Reason)"
        if ($gate.Decision -eq 'Rollback') {
            Write-Host 'Simulation routed traffic back to blue (100%).'
            return
        }
    }

    Write-Host 'Simulation promoted green to 100%.'
}
finally {
    foreach ($process in @($blue, $green)) {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id
            $process.WaitForExit(5000) | Out-Null
        }
    }
}
