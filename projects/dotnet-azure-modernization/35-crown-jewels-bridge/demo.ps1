# The three findings, live, against the three DLLs. About ninety seconds.
#
# Everything it prints is computed while you watch. A demo that prints numbers it was
# told is indistinguishable from one that prints numbers it found, and this project's
# entire claim is that these are measurements.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$exe = Join-Path $PSScriptRoot 'Bridge.Report\bin\Release\net10.0\Bridge.Report.exe'
if (-not (Test-Path $exe)) {
    Write-Host "Bridge.Report.exe is not built. Running build.ps1 first." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'build.ps1')
}

& $exe demo
exit $LASTEXITCODE
