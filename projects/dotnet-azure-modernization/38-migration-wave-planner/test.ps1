#!/usr/bin/env pwsh
# Full verification: syntax, tests, mutation sanity, and a byte-identical
# rebuild of docs/results.md.
#
#   ./test.ps1            tests + report rebuild
#   ./test.ps1 -Quick     tests only

param([switch]$Quick)

$ErrorActionPreference = "Stop"
$py = "C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe"
if (-not (Test-Path $py)) { $py = "python" }
Push-Location $PSScriptRoot
$failed = $false

function Step($name, $block) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { $script:failed = $true; Write-Host "FAILED: $name" -ForegroundColor Red }
}

try {
    Step "compile" { & $py -m compileall -q wave tests run_planner.py }
    Step "lint"    { & $py -m pyflakes wave tests run_planner.py }
    Step "tests"   { & $py -m pytest tests -q }

    # A suite that passes against a broken model is not a suite. Break an
    # estate invariant and confirm the tests notice.
    Write-Host "`n=== mutation sanity ===" -ForegroundColor Cyan
    $probe = @'
import sys, dataclasses
from wave.estate import build_estate
from wave.units import contract, infeasible_units

e = build_estate()
base = infeasible_units(e, contract(e))
if not base:
    print("baseline has no infeasible units -- the finding has evaporated")
    sys.exit(1)

# Capacity is rate * MAX_WAVE_MONTHS. Give every team a hundred times the
# throughput and the "no plan exists" finding must disappear; if it does not,
# the finding is not actually driven by capacity and the report is wrong.
fast = dataclasses.replace(e, rate={t: r * 100 for t, r in e.rate.items()})
if infeasible_units(fast, contract(fast)):
    print("mutation had no effect -- the infeasibility finding is not load bearing")
    sys.exit(1)
print(f"ok: {len(base)} infeasible units at declared capacity, 0 at 100x")
'@
    $probe | & $py -
    if ($LASTEXITCODE -ne 0) { $failed = $true; Write-Host "FAILED: mutation sanity" -ForegroundColor Red }

    if (-not $Quick) {
        Step "report rebuild" {
            $tmp = Join-Path ([IO.Path]::GetTempPath()) "wave-results-$PID.md"
            & $py run_planner.py --out $tmp | Out-Null
            if ($LASTEXITCODE -ne 0) { return }
            $a = (Get-FileHash docs\results.md -Algorithm SHA256).Hash
            $b = (Get-FileHash $tmp -Algorithm SHA256).Hash
            Remove-Item $tmp -ErrorAction SilentlyContinue
            if ($a -ne $b) {
                Write-Host "docs/results.md is stale -- run: python run_planner.py" -ForegroundColor Red
                $global:LASTEXITCODE = 1
            } else {
                Write-Host "report is byte-identical ($($a.Substring(0,16)))" -ForegroundColor Green
                $global:LASTEXITCODE = 0
            }
        }

        Write-Host "`n=== prediction parity ===" -ForegroundColor Cyan
        $p = (Select-String -Path docs\results.md -Pattern '\*\*Predicted\.\*\*' -AllMatches).Count
        $f = (Select-String -Path docs\results.md -Pattern '\*\*Found' -AllMatches).Count
        $w = (Select-String -Path docs\results.md -Pattern 'prediction wrong' -AllMatches).Count
        Write-Host "$p predictions, $f findings, $w contradicted"
        if ($p -ne $f) { $failed = $true; Write-Host "FAILED: prediction/finding mismatch" -ForegroundColor Red }
        if ($w -lt 3)  { $failed = $true; Write-Host "FAILED: too few contradictions to be a real experiment" -ForegroundColor Red }
    }
}
finally { Pop-Location }

if ($failed) { Write-Host "`nFAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nAll checks passed" -ForegroundColor Green
