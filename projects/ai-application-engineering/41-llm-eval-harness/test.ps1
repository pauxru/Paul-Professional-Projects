<#
.SYNOPSIS
    Six-stage verification for the LLM evaluation harness.

.DESCRIPTION
    Stages run cheapest-first so a failure surfaces as early as possible.

      1. lint          pyflakes over the library, the driver and the tests
      2. unit          the fast suite: stats, dataset, agreement, systems, gate
      3. mutation      three deliberate breakages that MUST fail the suite
      4. report        regenerate docs/results.md and compare bytes
      5. integrity     structural assertions on the generated document
      6. docs          every ADR and essay present and non-trivial

    Stage 3 is the one that matters. A test suite that passes is evidence of
    nothing unless breaking the code is known to break it.

.PARAMETER SkipSlow
    Skip stages 3 and 4, which take several minutes each.
#>
[CmdletBinding()]
param([switch]$SkipSlow)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$py = 'C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe'
if (-not (Test-Path $py)) { $py = 'python' }

$failures = @()
$TestTotal = 0
function Stage([string]$name, [scriptblock]$body) {
    Write-Host ""
    Write-Host "=== $name ===" -ForegroundColor Cyan
    try {
        & $body
        Write-Host "    PASS" -ForegroundColor Green
    } catch {
        Write-Host "    FAIL: $_" -ForegroundColor Red
        $script:failures += $name
    }
}

Push-Location $root
try {

Stage "1. lint" {
    & $py -m pyflakes evalharness run_eval.py tests
    if ($LASTEXITCODE -ne 0) { throw "pyflakes reported problems" }
}

Stage "2. unit" {
    # Accumulate rather than overwrite: stage 5 runs a different file, deliberately
    # excluded here, so the suite total is the sum of the two. (The mutation stage
    # below re-runs these same tests, which is why its output goes to null -- counting
    # it would multiply the total by the number of mutants.)
    & $py -m pytest tests -q --ignore=tests/test_results_integrity.py 2>&1 |
        Tee-Object -Variable unitOut | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "unit tests failed" }
    $script:TestTotal += [int][regex]::Match(($unitOut -join "`n"), '(\d+)\s+passed').Groups[1].Value
}

if (-not $SkipSlow) {

Stage "3. mutation" {
    # Each mutation is a plausible edit that must be caught. If the suite
    # still passes with one applied, the corresponding test is decorative.
    $mutations = @(
        @{ File = 'evalharness\stats.py'
           From = 'return min(1.0, 2.0 * tail)'
           To   = 'return min(1.0, tail)'
           Why  = 'sign test loses its two-sided factor' },

        @{ File = 'evalharness\agreement.py'
           From = 'return self.baseline_margin > 0 and self.baseline_margin_p < 0.05'
           To   = 'return self.baseline_margin > 0'
           Why  = 'informative reverts to comparing point estimates (bug 5)' },

        @{ File = 'evalharness\systems.py'
           From = 'f"{response.item_id}|{response.text}"'
           To   = 'response.item_id'
           Why  = 'judge noise cancels in paired differences again (bug 1)' }
    )

    foreach ($m in $mutations) {
        $path = Join-Path $root $m.File
        $original = [IO.File]::ReadAllText($path)
        if (-not $original.Contains($m.From)) {
            throw "mutation target not found in $($m.File): $($m.From)"
        }
        [IO.File]::WriteAllText($path, $original.Replace($m.From, $m.To))
        try {
            & $py -m pytest tests -q --ignore=tests/test_results_integrity.py `
                  -x --no-header 2>&1 | Out-Null
            $survived = ($LASTEXITCODE -eq 0)
        } finally {
            [IO.File]::WriteAllText($path, $original)
        }
        if ($survived) { throw "mutation SURVIVED: $($m.Why)" }
        Write-Host "    killed: $($m.Why)" -ForegroundColor DarkGray
    }
}

Stage "4. report" {
    & $py run_eval.py --out docs\results.md
    if ($LASTEXITCODE -ne 0) { throw "report generation failed" }
}

}

Stage "5. integrity" {
    $filter = if ($SkipSlow) { 'not Reproducibility and not Quick' } else { 'not Quick' }
    & $py -m pytest tests/test_results_integrity.py -q -k $filter 2>&1 |
        Tee-Object -Variable integrityOut | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "report integrity checks failed" }
    $script:TestTotal += [int][regex]::Match(($integrityOut -join "`n"), '(\d+)\s+passed').Groups[1].Value
}

Stage "6. docs" {
    $required = @(
        'README.md',
        'docs\results.md',
        'docs\known-limitations.md',
        'docs\adr\001-simulate-the-systems-under-test.md',
        'docs\adr\002-shared-and-private-variance.md',
        'docs\adr\003-judge-error-decomposition.md',
        'docs\adr\004-underpowered-as-a-verdict.md',
        'docs\adr\005-content-addressed-eval-sets.md',
        'docs\portfolio\01-no-significant-difference.md',
        'docs\portfolio\02-your-judge-is-a-constant.md',
        'docs\portfolio\03-testing-a-measuring-instrument.md',
        'docs\portfolio\04-bugs-the-experiment-found.md'
    )
    foreach ($f in $required) {
        $p = Join-Path $root $f
        if (-not (Test-Path $p)) { throw "missing: $f" }
        if ((Get-Item $p).Length -lt 1500) { throw "too short to be real: $f" }
    }
    Write-Host "    $($required.Count) documents present" -ForegroundColor DarkGray
}

} finally { Pop-Location }

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "FAILED: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "all 6 stages passed -- $script:TestTotal tests" -ForegroundColor Green
exit 0
