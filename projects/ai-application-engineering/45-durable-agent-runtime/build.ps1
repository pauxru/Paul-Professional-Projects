#!/usr/bin/env pwsh
# Type-checks and loads every module. There is no compiler here on purpose -- see
# docs/adr/003-no-build-step.md -- so "build" means: does Node accept and load the code?
# Node's strip-only TypeScript rejects any syntax that would need code generation, which
# makes a successful load a real check rather than a formality.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host '== 45-durable-agent-runtime :: build ==' -ForegroundColor Cyan

$node = (node --version)
Write-Host "node $node"
if ($node -notmatch '^v(2[2-9]|[3-9][0-9])\.') {
    throw "Node 22 or newer is required for native TypeScript execution; found $node"
}

$modules = @(
    'src/journal.ts'
    'src/ledger.ts'
    'src/runtime.ts'
    'src/workflows.ts'
    'src/experiments.ts'
    'src/predictions.ts'
    'src/report.ts'
)

foreach ($m in $modules) {
    if (-not (Test-Path $m)) { throw "missing module: $m" }
}

node src/loadcheck.ts
if ($LASTEXITCODE -ne 0) { throw 'one or more modules failed to load' }

# The report generator is the only entry point that writes files; running it here means a
# broken generator fails the build rather than the docs stage.
node src/main.ts | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'report generation failed' }

Write-Host "build ok -- $($modules.Count) modules loaded, docs regenerated" -ForegroundColor Green
