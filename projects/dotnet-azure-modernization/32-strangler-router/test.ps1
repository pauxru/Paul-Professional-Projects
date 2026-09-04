#Requires -Version 5.1
<#
.SYNOPSIS
    Vets and tests the strangler router.
#>
param(
    [switch]$Race,
    [int]$Runs = 1
)

$ErrorActionPreference = 'Stop'
$env:GOROOT = 'C:\Users\rukwaropaul\toolchains\go'
$env:GOPATH = 'C:\Users\rukwaropaul\go'
$env:GOPROXY = 'off'
$env:PATH = "$env:GOROOT\bin;$env:PATH"

Push-Location $PSScriptRoot
try {
    Write-Host 'go vet ./...' -ForegroundColor Cyan
    & go vet ./...
    if ($LASTEXITCODE -ne 0) { throw 'go vet failed' }

    $testArgs = @('test', '-count=1', './...')
    if ($Race) { $testArgs = @('test', '-count=1', '-race', './...') }

    for ($i = 1; $i -le $Runs; $i++) {
        Write-Host "go $($testArgs -join ' ')  (run $i of $Runs)" -ForegroundColor Cyan
        & go @testArgs
        if ($LASTEXITCODE -ne 0) { throw "tests failed on run $i" }
    }
    Write-Host 'PASS' -ForegroundColor Green
}
finally {
    Pop-Location
}
