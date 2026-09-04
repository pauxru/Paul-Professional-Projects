# Runs the experiment and prints the report.
#
#   ./demo.ps1          print to the console
#   ./demo.ps1 -Save    write docs/results.md

param([switch]$Save)

$ErrorActionPreference = "Stop"

$env:GOROOT = "C:\Users\rukwaropaul\toolchains\go"
$env:GOPATH = "C:\Users\rukwaropaul\go"
$env:GOPROXY = "off"
$env:PATH = "$env:GOROOT\bin;$env:PATH"

Set-Location $PSScriptRoot

if ($Save) {
    & go run ./cmd/semcache -out docs\results.md
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Write-Host "wrote docs\results.md" -ForegroundColor Green
} else {
    & go run ./cmd/semcache
    if ($LASTEXITCODE -ne 0) { exit 1 }
}
