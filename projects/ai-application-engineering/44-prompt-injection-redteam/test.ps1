# Six stages. Each one can fail independently and each says what it proved.
$ErrorActionPreference = "Stop"
$py = "C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe"
Set-Location $PSScriptRoot
$failed = @()
$TestTotal = 0

function Stage($n, $name, $block) {
    Write-Host ""
    Write-Host "[$n/6] $name" -ForegroundColor Cyan
    try { & $block; Write-Host "      OK" -ForegroundColor Green }
    catch { Write-Host "      FAILED: $_" -ForegroundColor Red; $script:failed += $name }
}

Stage 1 "Lint (pyflakes: unused imports, undefined names, dead f-strings)" {
    & $py -m pyflakes redteam tests run_redteam.py
    if ($LASTEXITCODE -ne 0) { throw "pyflakes reported issues" }
}

Stage 2 "Tests" {
    # Tee the run so the count survives into the summary line at the end. Stage 3 and
    # stage 6 re-run subsets of this same suite, so this is the only stage whose count
    # is the suite total -- summing every "N passed" in the output would report roughly
    # double the tests that actually exist.
    & $py -m pytest tests -q 2>&1 | Tee-Object -Variable pytestOut | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "pytest failed" }
    $m = [regex]::Match(($pytestOut -join "`n"), '(\d+)\s+passed')
    if ($m.Success) { $script:TestTotal = [int]$m.Groups[1].Value }
}

Stage 3 "Report freshness (docs/results.md matches the code that built it)" {
    & $py -m pytest tests/test_results_integrity.py -q
    if ($LASTEXITCODE -ne 0) { throw "docs/results.md is stale - run python run_redteam.py" }
}

Stage 4 "Determinism (three separate processes must agree byte for byte)" {
    $hashes = 1..3 | ForEach-Object {
        & $py -c "import run_redteam,hashlib;print(hashlib.sha256(run_redteam.build_report().render().encode()).hexdigest()[:16])"
    }
    if (($hashes | Select-Object -Unique).Count -ne 1) {
        throw "report is not reproducible across processes: $($hashes -join ', ')"
    }
    Write-Host "      sha $($hashes[0]) x3"
}

Stage 5 "Mutation check (the suite must notice a defence being disabled)" {
    # A test suite that passes with a control removed is not testing the
    # control. Five mutations, each reverting a real defect that shipped.
    #
    # Deliberately NOT mutated: the broker's `if not value` guard on empty
    # sensitive arguments. It is redundant with `trust_of_substring`'s
    # empty-needle fix, so removing it is an equivalent mutant -- see
    # tests/test_broker.py::test_the_empty_argument_is_guarded_twice_on_purpose.
    $mutations = @(
        @{ file = "redteam/broker.py";   from = '            if level is None:'; to = '            if False:' },
        @{ file = "redteam/pipeline.py"; from = 'if Layer.SPOTLIGHT in self.layers and channel < Trust.USER:'; to = 'if Layer.SPOTLIGHT in self.layers:' },
        @{ file = "redteam/channels.py"; from = '        if not needle:'; to = '        if False:' },
        @{ file = "redteam/normalize.py"; from = '    text, control = _strip_chars(text, controls)'; to = '    _, control = _strip_chars(text, controls)' },
        @{ file = "redteam/egress.py";   from = '            if self._host_allowed(host):'; to = '            if True:' }
    )
    $survivors = @()
    foreach ($m in $mutations) {
        $original = Get-Content $m.file -Raw
        if (-not $original.Contains($m.from)) { throw "mutation target not found in $($m.file): $($m.from)" }
        try {
            $original.Replace($m.from, $m.to) | Set-Content $m.file -NoNewline
            & $py -m pytest tests -q --tb=no 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { $survivors += "$($m.file): $($m.from)" }
        } finally { $original | Set-Content $m.file -NoNewline }
    }
    if ($survivors.Count -gt 0) { throw "mutations survived:`n  $($survivors -join "`n  ")" }
    Write-Host "      5/5 mutations killed"
}

Stage 6 "No secrets, no network calls, no model API" {
    $bad = Select-String -Path (Get-ChildItem -Recurse -Include *.py) `
        -Pattern 'sk-[A-Za-z0-9]{16,}|api[_-]?key\s*=\s*["''][^"'']{8,}|requests\.|urllib\.request|openai|anthropic' `
        -ErrorAction SilentlyContinue
    if ($bad) { throw "found: $($bad | ForEach-Object { $_.Path + ':' + $_.LineNumber })" }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "all 6 stages passed -- $script:TestTotal tests" -ForegroundColor Green
