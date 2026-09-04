#!/usr/bin/env pwsh
# Run the planner.
#
#   ./demo.ps1                 print the headline findings
#   ./demo.ps1 -Section 6      print one section of the report
#   ./demo.ps1 -Save           regenerate docs/results.md
#   ./demo.ps1 -MaxExact 7     shorten the exact calibration (faster)

param(
    [string]$Section,
    [switch]$Save,
    [int]$MaxExact = 9
)

$ErrorActionPreference = "Stop"
$py = "C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe"
if (-not (Test-Path $py)) { $py = "python" }
Push-Location $PSScriptRoot

try {
    if ($Save) {
        & $py run_planner.py --out docs\results.md --max-exact $MaxExact
        exit $LASTEXITCODE
    }

    if ($Section) {
        & $py run_planner.py --section $Section --max-exact $MaxExact
        exit $LASTEXITCODE
    }

    if (-not (Test-Path docs\results.md)) {
        Write-Host "docs/results.md is missing; generating it first." -ForegroundColor Yellow
        & $py run_planner.py --out docs\results.md --max-exact $MaxExact | Out-Null
    }

    Write-Host "`nMigration Wave Planner" -ForegroundColor Cyan
    Write-Host "Full run: ./demo.ps1 -Save   |   one section: ./demo.ps1 -Section 6`n"

    $lines = Get-Content docs\results.md
    $i = 0
    foreach ($l in $lines) {
        if ($l -match '^\*\*Found') {
            $colour = if ($l -match 'prediction wrong') { "Yellow" } else { "Green" }
            # A finding runs until the next blank line.
            $j = $i
            $buf = @()
            while ($j -lt $lines.Count -and $lines[$j].Trim() -ne "") { $buf += $lines[$j]; $j++ }
            Write-Host (($buf -join " ") -replace '\*\*','') -ForegroundColor $colour
            Write-Host ""
        }
        $i++
    }

    $tail = $lines | Select-Object -Last 4
    Write-Host ($tail -join " ").Trim() -ForegroundColor Cyan
}
finally { Pop-Location }
