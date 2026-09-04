$ErrorActionPreference = "Stop"
& "$PSScriptRoot\cargo.ps1" build --offline --release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "==> built"
