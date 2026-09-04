# Narrated walkthrough. Everything printed here is computed live -- no cached
# output, no fixtures.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host ""
Write-Host "  Saga Extractor -- cutting a transaction into a saga, and proving it fits"
Write-Host ""

dotnet build -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "build failed" -ForegroundColor Red; exit 1 }

# The '--' is quoted deliberately: PowerShell's parameter binder consumes a bare
# separator before dotnet ever sees it, and --demo would silently be dropped.
dotnet run --project src\Sagas.Report -c Release --no-build -v q --nologo '--' --demo
exit $LASTEXITCODE
