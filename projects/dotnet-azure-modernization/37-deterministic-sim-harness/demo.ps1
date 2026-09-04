$ErrorActionPreference = "Stop"
& "$PSScriptRoot\cargo.ps1" run --offline --release --quiet > "$PSScriptRoot\docs\results.md"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "==> wrote docs/results.md"
Get-Content "$PSScriptRoot\docs\results.md" -TotalCount 20
