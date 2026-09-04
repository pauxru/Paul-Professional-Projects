# Runs the full verification for 43-retrieval-lab.
#
# The bare `python` on this machine is a Windows Store stub; the real 3.12
# interpreter is addressed by full path. NumPy is the only third-party
# dependency and it is already installed there.
#
# The last check is the one that matters most: docs/results.md is regenerated
# from scratch and hashed against the committed copy. Every number in the
# report is a counted operation or a deterministic statistic (ADR 0004), so a
# fresh run must be byte-identical. If it is not, either the report is stale or
# something in the pipeline is non-deterministic -- both are defects.

$ErrorActionPreference = "Stop"

$py = "C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe"
if (-not (Test-Path $py)) {
    Write-Host "Python 3.12 not found at $py" -ForegroundColor Red
    exit 1
}

Set-Location $PSScriptRoot

Write-Host "== interpreter ==" -ForegroundColor Cyan
& $py --version
& $py -c "import numpy; print('numpy', numpy.__version__)"
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n== compile all modules ==" -ForegroundColor Cyan
& $py -m compileall -q rqlab tests run_lab.py
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host "clean"

Write-Host "`n== pytest ==" -ForegroundColor Cyan
# Tee the count out of this run. The stage below deliberately re-runs a subset of
# these same tests in two orders, so this is the only stage whose number is the suite
# total; adding up every "N passed" the script prints would overstate it by ~85%.
& $py -m pytest -q 2>&1 | Tee-Object -Variable pytestOut | Write-Host
if ($LASTEXITCODE -ne 0) { exit 1 }
$TestTotal = [int][regex]::Match(($pytestOut -join "`n"), '(\d+)\s+passed').Groups[1].Value

Write-Host "`n== shared fixtures are not mutated ==" -ForegroundColor Cyan
# Session-scoped fixtures build the corpus once and share it across every test.
# If any test mutates that shared state, running a subset in a different order
# produces a different result. Three files that all consume the corpus, run
# alone and in reverse order.
& $py -m pytest -q tests/test_experiment.py tests/test_chunking.py tests/test_corpus.py
if ($LASTEXITCODE -ne 0) { exit 1 }
& $py -m pytest -q tests/test_corpus.py tests/test_chunking.py tests/test_experiment.py
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n== report is reproducible ==" -ForegroundColor Cyan
$tmp = Join-Path $env:TEMP "rqlab-results-check.md"
& $py run_lab.py --out $tmp --quiet
if ($LASTEXITCODE -ne 0) { exit 1 }
$a = (Get-FileHash docs\results.md).Hash
$b = (Get-FileHash $tmp).Hash
Remove-Item $tmp
if ($a -ne $b) {
    Write-Host "docs/results.md does not match a fresh run." -ForegroundColor Red
    Write-Host "The report claims to be deterministic; regenerate with demo.ps1 -Save." -ForegroundColor Red
    exit 1
}
Write-Host "byte-identical to docs/results.md"

Write-Host "`n== no open predictions ==" -ForegroundColor Cyan
# The report DSL raises if a section calls found() without expect(), but a
# section that calls neither would slip through silently.
$expects = (Select-String -Path docs\results.md -Pattern '^\*\*Predicted\.\*\*' -AllMatches).Count
$founds  = (Select-String -Path docs\results.md -Pattern '^\*\*Found' -AllMatches).Count
Write-Host "predictions: $expects   findings: $founds"
if ($expects -ne $founds) {
    Write-Host "every prediction must have exactly one finding" -ForegroundColor Red
    exit 1
}

Write-Host "`nall 6 stages passed -- $TestTotal tests" -ForegroundColor Green
