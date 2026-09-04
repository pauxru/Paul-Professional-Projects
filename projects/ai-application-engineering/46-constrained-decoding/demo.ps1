# Regenerates every measured claim in docs/. Nothing in this repository is
# hand-typed: if a number appears in the documentation it came from here.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$bench = Join-Path $root 'build\cdec_bench.exe'
$venvPython = Join-Path $root '.venv\Scripts\python.exe'
if (-not (Test-Path $bench)) { throw 'run build.ps1 first' }
if (-not (Test-Path $venvPython)) { throw 'run build.ps1 first' }

New-Item -ItemType Directory -Force -Path (Join-Path $root 'docs') | Out-Null

Write-Host '==> native benchmarks (compilation, masking, caching, deduplication)'
& $bench | Tee-Object -FilePath (Join-Path $root 'docs\benchmark-results.md')
if ($LASTEXITCODE -ne 0) { throw 'benchmark failed' }

Write-Host ''
Write-Host '==> distribution experiments (exact, not sampled)'
# The package is not installed into the venv on purpose: it has no build step
# and no dependencies, so pointing at the source tree keeps the demo honest
# about what is being run.
$env:PYTHONPATH = Join-Path $root 'python'
& $venvPython -m schemafsm.experiments --samples 200 |
    Tee-Object -FilePath (Join-Path $root 'docs\experiment-results.md')
if ($LASTEXITCODE -ne 0) { throw 'experiments failed' }

Write-Host ''
Write-Host '==> wrote docs/benchmark-results.md and docs/experiment-results.md'
