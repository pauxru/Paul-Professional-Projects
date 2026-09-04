#!/usr/bin/env pwsh
# Seven stages. Anything less than all seven passing means the numbers in docs/results.md
# are not evidence of anything.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$py = 'C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe'
if (-not (Test-Path $py)) { $py = 'python' }

$started = Get-Date
$stage = 0
function Stage([string]$name) {
    $script:stage++
    Write-Host ''
    Write-Host "-- stage $script:stage : $name" -ForegroundColor Cyan
}

Write-Host '== 50-silent-failure-observability :: test ==' -ForegroundColor Cyan

# ---------------------------------------------------------------------------
Stage 'unit and property tests'
$output = & $py -m pytest tests/ -q --no-header 2>&1
if ($LASTEXITCODE -ne 0) {
    $output | Select-Object -Last 40
    throw 'unit tests failed'
}
$testCount = 0
foreach ($line in $output) {
    if ($line -match '(\d+) passed') { $testCount = [int]$Matches[1] }
}
if ($testCount -lt 120) { throw "expected at least 120 tests, found $testCount" }
Write-Host "  $testCount tests passed" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Stage 'report determinism'
# Every number in this project comes from seeded generators. If two runs disagree, some
# measurement is reading wall-clock or hash-salt state and the report is not evidence.
& $py -m src.main --out docs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'report generation failed' }
$first = Get-FileHash docs/results-stable.md -Algorithm SHA256
$firstJson = Get-FileHash docs/dashboard-data.json -Algorithm SHA256
& $py -m src.main --out docs | Out-Null
$second = Get-FileHash docs/results-stable.md -Algorithm SHA256
$secondJson = Get-FileHash docs/dashboard-data.json -Algorithm SHA256
if ($first.Hash -ne $second.Hash) {
    throw 'the stable report is not byte-identical across runs'
}
if ($firstJson.Hash -ne $secondJson.Hash) {
    throw 'the dashboard data is not byte-identical across runs'
}
Write-Host "  stable report sha256 $($first.Hash.Substring(0,16))..." -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Stage 'report content checks'
$report = Get-Content docs/results.md -Raw
foreach ($required in @(
        '## The scenarios',
        '## Detection matrix',
        '## What each detector costs',
        '## The cheapest set that leaves nothing uncovered',
        '## Predictions:',
        '## The five findings worth carrying out of here',
        'Every request in every scenario returns HTTP 200')) {
    if ($report -notmatch [regex]::Escape($required)) { throw "results.md is missing: $required" }
}
if ($report -match 'TODO|FIXME|TBD|XXX|Lorem ipsum') { throw 'results.md contains placeholder text' }
$contradicted = ([regex]::Matches($report, '\*\*contradicted\*\*')).Count
if ($contradicted -lt 5) { throw "only $contradicted predictions were contradicted; the scoreboard is not doing any work" }
if ($report -notmatch 'false-alarm days on healthy|raised \*\*0\*\* alerting days') {
    if ($report -notmatch 'the entire panel raised \*\*0\*\* alerting days') {
        throw 'results.md no longer reports a zero false-alarm panel'
    }
}
Write-Host "  6 sections, $contradicted contradicted predictions" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Stage 'dashboard build and determinism'
node dashboard/build.ts | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dashboard build failed' }
$firstHtml = Get-FileHash docs/dashboard.html -Algorithm SHA256
node dashboard/build.ts | Out-Null
$secondHtml = Get-FileHash docs/dashboard.html -Algorithm SHA256
if ($firstHtml.Hash -ne $secondHtml.Hash) { throw 'the dashboard is not byte-identical across runs' }
$html = Get-Content docs/dashboard.html -Raw
foreach ($required in @('<svg', 'Detecting failures that don', 'control &mdash; alerting here is wrong')) {
    if ($html -notmatch [regex]::Escape($required)) { throw "dashboard.html is missing: $required" }
}
# The page must not fetch anything or run any script: it is an artefact, not an app.
if ($html -match '<script|https?://') { throw 'the dashboard must be self-contained -- no scripts, no network' }
$svgCount = ([regex]::Matches($html, '<svg')).Count
if ($svgCount -lt 40) { throw "expected at least 40 charts, found $svgCount" }
Write-Host "  $svgCount inline charts, no scripts, no network" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Stage 'mutation testing'
& $py tools/mutate.py
if ($LASTEXITCODE -ne 0) { throw 'a mutant survived' }

# ---------------------------------------------------------------------------
Stage 'dependency surface'
# numpy is the only third-party import, and the TypeScript half has none at all. That is
# a claim on the tin, so it is a test rather than a sentence in the README.
if (Test-Path node_modules) { throw 'node_modules exists; the dashboard must have no dependencies' }
$allowed = @('numpy', 'pytest')
# Imports are extracted by parsing, not by matching text: a docstring beginning "from the
# healthy baseline ..." is an import of a module named `the` to any regex, and this
# project's prose contains three such lines. See tools/imports.py.
$stdlib = @('__future__','abc','argparse','ast','collections','dataclasses','functools','hashlib','inspect','itertools','json','math','os','pathlib','random','re','shutil','subprocess','sys','tempfile','time','typing','src','tests','tools')
$imports = & $py tools/imports.py
if ($LASTEXITCODE -ne 0) { throw 'failed to parse the import graph' }
foreach ($line in $imports) {
    $mod, $file = $line -split "`t"
    if ($stdlib -contains $mod -or $allowed -contains $mod) { continue }
    throw "unexpected import '$mod' in $file -- this project depends only on $($allowed -join ', ')"
}
$tsImports = Select-String -Path dashboard/*.ts -Pattern 'from "([^."][^"]*)"' -AllMatches
foreach ($m in $tsImports.Matches) {
    if (-not $m.Groups[1].Value.StartsWith('node:')) {
        throw "non-builtin TypeScript import '$($m.Groups[1].Value)'"
    }
}
Write-Host "  python: numpy + pytest only; typescript: node: builtins only" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Stage 'secrets scan'
$universal = @(
    'AKIA[0-9A-Z]{16}',
    '-----BEGIN (RSA|EC|OPENSSH|PRIVATE)',
    'xox[baprs]-[0-9A-Za-z-]{10,}',
    'gh[pousr]_[0-9A-Za-z]{36}'
)
# Production code is held to a stricter standard than tests and fixtures.
$productionOnly = @(
    '(?i)(api[_-]?key|client[_-]?secret|password)\s*[:=]\s*["''][^"''\s]{8,}["'']',
    '(?i)connectionstring\s*[:=]\s*["''][^"'']+;'
)
$all = Get-ChildItem -Recurse -File -Include *.py, *.ts, *.ps1, *.md, *.json |
    Where-Object { $_.FullName -notmatch '__pycache__|\\node_modules\\' }
$production = $all | Where-Object { $_.FullName -match '\\src\\|\\dashboard\\' }

$findings = @()
foreach ($p in $universal) { $findings += Select-String -Path $all.FullName -Pattern $p -AllMatches }
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
