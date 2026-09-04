# Six stages. Each one can fail independently and each says what it proved.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$failed = @()
$TestTotal = 0

function Stage($n, $name, $block) {
    Write-Host ""
    Write-Host "[$n/6] $name" -ForegroundColor Cyan
    try { & $block; Write-Host "      OK" -ForegroundColor Green }
    catch { Write-Host "      FAILED: $_" -ForegroundColor Red; $script:failed += $name }
}

Stage 1 "Lint (clippy, warnings denied)" {
    # `-Dwarnings` must be one token: passed as `-D warnings`, cargo forwards
    # the two halves separately and rustc reads `warnings` as an input filename.
    $out = & .\cargo.ps1 clippy --offline --all-targets '--' -Dwarnings 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 30; throw "clippy reported issues" }
}

Stage 2 "Tests" {
    $out = & .\cargo.ps1 test --offline --release 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 40; throw "cargo test failed" }
    $counts = $out | Select-String 'test result: ok\. (\d+) passed' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value }
    $script:TestTotal = ($counts | Measure-Object -Sum).Sum
    Write-Host "      $script:TestTotal tests"
}

Stage 3 "Report freshness (docs/results.md matches the code that built it)" {
    $out = & .\cargo.ps1 test --offline --release --test results_integrity 2>&1
    if ($LASTEXITCODE -ne 0) {
        $out | Select-Object -Last 20
        throw "docs/results.md is stale - run cargo run --release --bin run_gateway"
    }
}

Stage 4 "Determinism (three separate processes must agree byte for byte)" {
    # The report is written from a fixed seed. If two processes disagree,
    # something in the pipeline is reading a clock, a hash seed, or an
    # unordered map -- and every number in the document is then unreproducible.
    $hashes = 1..3 | ForEach-Object {
        $text = & .\cargo.ps1 run --offline --release --quiet --bin run_gateway '--' --stdout 2>$null
        $bytes = [System.Text.Encoding]::UTF8.GetBytes(($text -join "`n"))
        $sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
        ([System.BitConverter]::ToString($sha) -replace '-','').Substring(0, 16).ToLower()
    }
    if (($hashes | Select-Object -Unique).Count -ne 1) {
        throw "report is not reproducible across processes: $($hashes -join ', ')"
    }
    Write-Host "      sha $($hashes[0]) x3"
}

Stage 5 "Mutation check (the suite must notice an invariant being removed)" {
    # A test suite that passes with a control removed is not testing the
    # control. Six mutations, each reverting a real defect that shipped during
    # this build -- see docs/portfolio/04-bugs-the-simulator-found.md.
    #
    # Deliberately NOT mutated: the `.max(req.prompt_tokens)` in Running::new.
    # Removing it is an equivalent mutant, because can_admit already refuses
    # anything whose prompt exceeds the reservation, so no reachable state
    # distinguishes the two.
    $mutations = @(
        # 1. KV double-count: charge the sum instead of the max.
        @{ file = "src\engine.rs"; from = 'self.kv_held.max(self.reserved_kv)'; to = 'self.kv_held + self.reserved_kv' },
        # 2. Admission headroom: ignore what the existing batch is about to claim.
        @{ file = "src\engine.rs"; from = '.filter(|r| !r.prefilling() && !r.done() && r.grows_next_token())'; to = '.filter(|_r| false)' },
        # 3. Truncation flag: report a ceiling-terminated run as if it drained.
        @{ file = "src\sim.rs"; from = '            truncated = true;'; to = '            truncated = false;' },
        # 4. Estimator channel separation: let the reservation reuse the scheduling estimate.
        @{ file = "src\sim.rs"; from = 'let kv_est = kv_estimator.estimate(&req);'; to = 'let kv_est = est;' },
        # 5. Preemption backoff: return a preempted request to the queue immediately.
        @{ file = "src\sim.rs"; from = 'q.not_before_us = now_us + step_us * (1u64 << n.min(20));'; to = 'q.not_before_us = now_us;' },
        # 6. Least-loaded fill: go back to filling replicas in index order.
        @{ file = "src\sim.rs"; from = '.min_by_key(|(i, s)| (s.replica.batch(), *i))'; to = '.min_by_key(|(i, _s)| *i)' }
    )
    $survivors = @()
    $missing = @()
    foreach ($m in $mutations) {
        $original = Get-Content $m.file -Raw
        if (-not $original.Contains($m.from)) { $missing += "$($m.file): $($m.from)"; continue }
        try {
            $original.Replace($m.from, $m.to) | Set-Content $m.file -NoNewline
            & .\cargo.ps1 test --offline --release 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { $survivors += "$($m.file): $($m.from)" }
        } finally { $original | Set-Content $m.file -NoNewline }
    }
    if ($missing.Count -gt 0) { throw "mutation targets not found:`n  $($missing -join "`n  ")" }
    if ($survivors.Count -gt 0) { throw "mutations survived:`n  $($survivors -join "`n  ")" }
    Write-Host "      $($mutations.Count)/$($mutations.Count) mutations killed"
}

Stage 6 "No dependencies, no secrets, no network" {
    # The claim on the README is that this crate has no dependencies. That is a
    # claim about the lockfile, not about intent.
    $manifest = Get-Content Cargo.toml -Raw
    if ($manifest -match '(?ms)^\s*\[dependencies\]\s*\r?\n\s*\w') {
        throw "Cargo.toml declares dependencies; the README says there are none"
    }
    $bad = Select-String -Path (Get-ChildItem -Recurse -Include *.rs, *.toml, *.ps1 |
            Where-Object { $_.FullName -notmatch '\\target\\' }) `
        -Pattern 'sk-[A-Za-z0-9]{16,}|api[_-]?key\s*=\s*["''][^"'']{8,}|https?://(?!github\.com|doc\.rust-lang)' `
        -ErrorAction SilentlyContinue
    if ($bad) { throw "found: $($bad | ForEach-Object { $_.Path + ':' + $_.LineNumber })" }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "all 6 stages passed -- $script:TestTotal tests" -ForegroundColor Green
