[CmdletBinding()]
param(
    [ValidateRange(3, 200)]
    [int]$Requests = 20,
    [ValidateRange(5, 90)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet build ProductionIncidentDiagnostics.slnx -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$scenarios = @(
    'INC-001', 'INC-002', 'INC-003', 'INC-004', 'INC-005',
    'INC-006', 'INC-007', 'INC-008', 'INC-009', 'INC-010'
)

foreach ($mode in @('broken', 'fixed')) {
    foreach ($scenario in $scenarios) {
        Write-Host "Running $scenario in $mode mode..."
        dotnet run --project src\Lab.Harness\Lab.Harness.csproj -c Release --no-build -- `
            --scenario $scenario --mode $mode --requests $Requests --timeout-seconds $TimeoutSeconds
        if ($LASTEXITCODE -ne 0) {
            throw "$scenario ($mode) failed with exit code $LASTEXITCODE."
        }
    }
}

Write-Host 'All incident evidence has been regenerated.'
