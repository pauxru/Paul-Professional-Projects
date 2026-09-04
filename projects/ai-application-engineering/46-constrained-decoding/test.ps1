# Runs both test suites. Exits non-zero if either fails.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$exe = Join-Path $root 'build\cdec_tests.exe'
$venvPython = Join-Path $root '.venv\Scripts\python.exe'

if (-not (Test-Path $exe)) { throw "run build.ps1 first: $exe is missing" }
if (-not (Test-Path $venvPython)) { throw 'run build.ps1 first: .venv is missing' }

Write-Host '==> C++ tests'
& $exe
$cppExit = $LASTEXITCODE

Write-Host ''
Write-Host '==> Python tests'
& $venvPython -m pytest
$pyExit = $LASTEXITCODE

if ($cppExit -ne 0 -or $pyExit -ne 0) {
    throw "tests failed (c++ exit $cppExit, python exit $pyExit)"
}
Write-Host ''
Write-Host '==> all tests passed'
