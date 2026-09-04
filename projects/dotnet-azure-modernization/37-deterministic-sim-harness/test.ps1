$ErrorActionPreference = "Stop"
& "$PSScriptRoot\cargo.ps1" test --offline --release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "==> all tests passed"
