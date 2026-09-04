#!/usr/bin/env pwsh
# Six stages. Anything less than all six passing means the numbers in docs/results.md are
# not evidence of anything.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$started = Get-Date
$stage = 0
function Stage([string]$name) {
    $script:stage++
    Write-Host ''
    Write-Host "-- stage $script:stage : $name" -ForegroundColor Cyan
}

Write-Host '== 45-durable-agent-runtime :: test ==' -ForegroundColor Cyan

# ---------------------------------------------------------------------------
Stage 'unit and property tests'
$output = node --test "tests/*.test.ts" 2>&1
$output | Where-Object { $_ -match '^(ℹ|✖|not ok)' } | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) {
    $output | Where-Object { $_ -match 'AssertionError|Error \[' } | Select-Object -First 20
    throw 'unit tests failed'
}
$testCount = 0
foreach ($line in $output) {
    if ($line -match '^ℹ pass (\d+)') { $testCount = [int]$Matches[1] }
}
if ($testCount -lt 100) { throw "expected at least 100 tests, found $testCount" }

# ---------------------------------------------------------------------------
Stage 'report determinism'
node src/main.ts | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'report generation failed' }
$first = Get-FileHash docs/results-stable.md -Algorithm SHA256
node src/main.ts | Out-Null
$second = Get-FileHash docs/results-stable.md -Algorithm SHA256
if ($first.Hash -ne $second.Hash) {
    throw 'the stable report is not byte-identical across runs; a measurement is leaking wall-clock state'
}
Write-Host "  stable report sha256 $($first.Hash.Substring(0,16))..." -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Stage 'report content checks'
$report = Get-Content docs/results.md -Raw
foreach ($required in @(
        '## 1\.', '## 2\.', '## 3\.', '## 4\.', '## 5\.', '## 6\.', '## 7\.', '## 8\.',
        'What this does not measure',
        'predictions were written before any experiment was run')) {
    if ($report -notmatch $required) { throw "results.md is missing: $required" }
}
if ($report -match 'TODO|FIXME|TBD|XXX|Lorem ipsum') { throw 'results.md contains placeholder text' }
$contradicted = ([regex]::Matches($report, '\| contradicted \|')).Count
if ($contradicted -lt 5) { throw "only $contradicted predictions were contradicted; the scoreboard is not doing any work" }
Write-Host "  8 sections, $contradicted contradicted predictions" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Stage 'mutation testing'
node tools/mutate.ts
if ($LASTEXITCODE -ne 0) { throw 'a mutant survived' }

# ---------------------------------------------------------------------------
Stage 'zero dependencies'
# The claim on the tin is that this runs on a stock Node with nothing installed. That is
# only true if it stays true, so it is a test rather than a sentence in the README.
if (Test-Path node_modules) { throw 'node_modules exists; this project must have no dependencies' }
if (Test-Path package-lock.json) { throw 'package-lock.json exists; this project must have no dependencies' }
$imports = Select-String -Path src/*.ts, tests/*.ts, tools/*.ts -Pattern "from '([^.'][^']*)'" -AllMatches
foreach ($m in $imports.Matches) {
    $spec = $m.Groups[1].Value
    if (-not $spec.StartsWith('node:')) { throw "non-builtin import '$spec' -- this project has no dependencies" }
}
Write-Host "  every import is relative or node: builtin" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Stage 'secrets scan'
$universal = @(
    'AKIA[0-9A-Z]{16}',
    '-----BEGIN (RSA|EC|OPENSSH|PRIVATE)',
    'xox[baprs]-[0-9A-Za-z-]{10,}',
    'gh[pousr]_[0-9A-Za-z]{36}'
)
# Production code is held to a stricter standard than tests and fixtures: a literal named
# `apiKey` is fine in a fixture and is a finding in src/.
$productionOnly = @(
    '(?i)(api[_-]?key|client[_-]?secret|password)\s*[:=]\s*["''][^"''\s]{8,}["'']',
    '(?i)connectionstring\s*[:=]\s*["''][^"'']+;'
)

$all = Get-ChildItem -Recurse -File -Include *.ts, *.ps1, *.md, *.json |
    Where-Object { $_.FullName -notmatch '\\node_modules\\' }
$production = $all | Where-Object { $_.FullName -match '\\src\\' }

$findings = @()
foreach ($p in $universal) {
    $findings += Select-String -Path $all.FullName -Pattern $p -AllMatches
}
foreach ($p in $productionOnly) {
    if ($production) { $findings += Select-String -Path $production.FullName -Pattern $p -AllMatches }
}
if ($findings) {
    $findings | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)" -ForegroundColor Red }
    throw "$($findings.Count) potential secret(s) found"
}
Write-Host "  clean across $($all.Count) files ($($production.Count) held to the production standard)" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
$elapsed = [int]((Get-Date) - $started).TotalSeconds
Write-Host ''
Write-Host "all $stage stages passed -- $testCount tests, ${elapsed}s" -ForegroundColor Green
