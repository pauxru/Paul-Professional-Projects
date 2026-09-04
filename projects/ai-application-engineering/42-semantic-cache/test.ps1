# Runs the full verification for 42-semantic-cache.
#
# Note the absence of -race. The Go race detector requires cgo and a C
# toolchain; this machine has neither, so -race cannot run at all (see
# docs/known-limitations.md). internal/flight is the only concurrent package
# here, so it gets a high-count stress run at elevated parallelism instead.
# That exercises interleavings repeatedly but proves nothing about
# happens-before, and is not a substitute.

$ErrorActionPreference = "Stop"

$env:GOROOT = "C:\Users\rukwaropaul\toolchains\go"
$env:GOPATH = "C:\Users\rukwaropaul\go"
$env:GOPROXY = "off"
$env:PATH = "$env:GOROOT\bin;$env:PATH"

Set-Location $PSScriptRoot

Write-Host "== gofmt ==" -ForegroundColor Cyan
$unformatted = & gofmt -l .
if ($unformatted) {
    Write-Host "not gofmt-clean:" -ForegroundColor Red
    $unformatted | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "clean"

Write-Host "`n== go vet ==" -ForegroundColor Cyan
& go vet ./...
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host "clean"

Write-Host "`n== go test ./... ==" -ForegroundColor Cyan
& go test ./... -count=1
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n== concurrency stress (internal/flight, 50 runs, GOMAXPROCS=8) ==" -ForegroundColor Cyan
$env:GOMAXPROCS = "8"
& go test ./internal/flight/ -count=50
if ($LASTEXITCODE -ne 0) { exit 1 }
Remove-Item Env:\GOMAXPROCS

Write-Host "`n== report is reproducible ==" -ForegroundColor Cyan
$tmp = Join-Path $env:TEMP "semcache-results-check.md"
& go run ./cmd/semcache -out $tmp | Out-Null
if ($LASTEXITCODE -ne 0) { exit 1 }
$a = (Get-FileHash docs\results.md).Hash
$b = (Get-FileHash $tmp).Hash
Remove-Item $tmp
if ($a -ne $b) {
    Write-Host "docs/results.md does not match a fresh run." -ForegroundColor Red
    Write-Host "The report claims to be deterministic; regenerate with demo.ps1 -Save." -ForegroundColor Red
    exit 1
}
Write-Host "byte-identical to docs/results.md"

Write-Host "`nALL CHECKS PASSED" -ForegroundColor Green
