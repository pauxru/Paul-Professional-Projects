#!/usr/bin/env pwsh
# Build = check the toolchain, byte-compile the Python, and generate the reports and the
# dashboard. There is nothing to link, so "build" here means "prove it runs end to end".

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$py = 'C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe'
if (-not (Test-Path $py)) { $py = 'python' }

Write-Host '== 50-silent-failure-observability :: build ==' -ForegroundColor Cyan

& $py --version
node --version

Write-Host ''
Write-Host '-- byte-compiling' -ForegroundColor Cyan
& $py -m compileall -q src tools tests
if ($LASTEXITCODE -ne 0) { throw 'compileall failed' }

Write-Host '-- running the panel' -ForegroundColor Cyan
& $py -m src.main --out docs
if ($LASTEXITCODE -ne 0) { throw 'panel run failed' }

Write-Host '-- building the dashboard' -ForegroundColor Cyan
node dashboard/build.ts
if ($LASTEXITCODE -ne 0) { throw 'dashboard build failed' }

Write-Host ''
Write-Host 'build ok' -ForegroundColor Green
