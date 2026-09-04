# scripts\demo.ps1 — end-to-end demo of the toolkit.
# Runs on Windows PowerShell 5.1+ or PowerShell 7+.

param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-Location $Root

if (-not $SkipBuild) {
    Write-Host "== Building solution ==" -ForegroundColor Cyan
    dotnet build -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

$sampleApiDll = Join-Path $Root 'src\SampleApi\bin\Release\net10.0\SampleApi.dll'
$loadrunDll   = Join-Path $Root 'src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll'
if (-not (Test-Path $sampleApiDll)) { throw "SampleApi.dll not found — build first" }
if (-not (Test-Path $loadrunDll))   { throw "loadrun.dll not found — build first" }

# Make sure port 5027 is free of a previous SampleApi.
Write-Host "== Starting SampleApi on http://127.0.0.1:5027 ==" -ForegroundColor Cyan
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5027'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$api = Start-Process -FilePath 'dotnet' -ArgumentList $sampleApiDll -PassThru -WindowStyle Hidden
try {
    # Wait for readiness (max 20 s).
    $ready = $false
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 1
        try {
            $r = Invoke-WebRequest -Uri http://127.0.0.1:5027/health/ready -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
    }
    if (-not $ready) { throw "SampleApi did not become ready within 20s" }

    Write-Host "== Reset pathology switches ==" -ForegroundColor Cyan
    Invoke-WebRequest -Method Delete -Uri http://127.0.0.1:5027/admin/pathology -UseBasicParsing | Out-Null

    Write-Host "== Baseline (pathological) run ==" -ForegroundColor Cyan
    dotnet $loadrunDll run scenarios\case-study-baseline.json --results results --out results

    Write-Host "== Reset pathology switches ==" -ForegroundColor Cyan
    Invoke-WebRequest -Method Delete -Uri http://127.0.0.1:5027/admin/pathology -UseBasicParsing | Out-Null

    Write-Host "== Optimised run ==" -ForegroundColor Cyan
    dotnet $loadrunDll run scenarios\case-study-optimised.json --results results --out results

    Write-Host "== Comparison ==" -ForegroundColor Cyan
    $baseline = Get-ChildItem results\case-study-baseline-*.json | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $candidate = Get-ChildItem results\case-study-optimised-*.json | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    dotnet $loadrunDll compare $baseline.FullName $candidate.FullName --out results
}
finally {
    Write-Host "== Stopping SampleApi ==" -ForegroundColor Cyan
    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force }
}
