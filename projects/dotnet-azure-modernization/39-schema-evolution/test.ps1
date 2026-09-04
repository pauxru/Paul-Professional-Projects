#!/usr/bin/env pwsh
# Full verification: build, vet, tests, mutation sanity, and a byte-identical
# rebuild of docs/results.md.
#
#   ./test.ps1            everything
#   ./test.ps1 -Quick     build, vet and unit tests only

param([switch]$Quick)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot

$env:GOROOT = "C:\Users\rukwaropaul\toolchains\go"
$env:GOPATH = "C:\Users\rukwaropaul\go"
$env:GOPROXY = "off"
$env:PATH = "$env:GOROOT\bin;$env:PATH"
if (-not (Test-Path $env:GOROOT)) { $env:PATH = $env:PATH }

$failed = $false

function Step($name, $block) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { $script:failed = $true; Write-Host "FAILED: $name" -ForegroundColor Red }
}

try {
    Step "gofmt"  {
        $drift = gofmt -l .
        if ($drift) { Write-Host "not gofmt-clean:`n$drift"; $global:LASTEXITCODE = 1 }
        else { Write-Host "clean"; $global:LASTEXITCODE = 0 }
    }
    Step "build"  { go build ./... }
    Step "vet"    { go vet ./... }
    Step "tests"  { go test ./... -short }

    if ($Quick) {
        if ($failed) { exit 1 }
        Write-Host "`nquick gate passed" -ForegroundColor Green
        exit 0
    }

    # A suite that passes against a broken model is not a suite. Break a
    # load-bearing invariant and confirm the tests notice.
    #
    # The chosen mutation is the conflict matrix's symmetry, because every
    # queueing result in the report depends on it and nothing else would
    # obviously break if it were wrong.
    Write-Host "`n=== mutation sanity ===" -ForegroundColor Cyan
    $src = "locks\modes.go"
    $orig = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $src))
    $mutated = $orig.Replace(
        "/* RowExclusive */ {false, false, false, false, true, true, true, true},",
        "/* RowExclusive */ {false, false, false, false, false, true, true, true},")
    if ($mutated -eq $orig) {
        Write-Host "mutation target not found -- update test.ps1" -ForegroundColor Red
        $failed = $true
    } else {
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot $src), $mutated)
        try {
            go test ./locks/... 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "the mutated conflict matrix still passes -- the tests are not checking it" -ForegroundColor Red
                $failed = $true
            } else {
                Write-Host "mutation detected" -ForegroundColor Green
            }
        } finally {
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot $src), $orig)
        }
    }

    # The report is the artefact. Every figure in it must be derived, so a
    # fresh run has to reproduce it byte for byte.
    Step "report reproducibility" { go test ./cmd/... -run TestResultsAreReproducible -v }

    # And the predictions in it must still be adjudicated by measurement
    # rather than by editing.
    Step "prediction integrity" { go test ./cmd/... -run "TestEveryPredictionIsAdjudicated|TestContradictionsSurvive" -v }

    Write-Host "`n=== report tally ===" -ForegroundColor Cyan
    go run ./cmd/evolve

} finally {
    Pop-Location
}

if ($failed) { Write-Host "`nGATE FAILED" -ForegroundColor Red; exit 1 }
Write-Host "`nall stages passed" -ForegroundColor Green
