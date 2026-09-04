#!/usr/bin/env pwsh
# Walk the report, one section at a time.
#
#   ./demo.ps1              the summary scoreboard
#   ./demo.ps1 -Section 2   print section 2 in full
#   ./demo.ps1 -List        list the sections

param([int]$Section = 0, [switch]$List)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $path = "docs\results.md"
    if (-not (Test-Path $path)) {
        Write-Host "docs/results.md missing; generating..." -ForegroundColor Yellow
        $env:GOROOT = "C:\Users\rukwaropaul\toolchains\go"
        $env:GOPATH = "C:\Users\rukwaropaul\go"
        $env:GOPROXY = "off"
        $env:PATH = "$env:GOROOT\bin;$env:PATH"
        go run ./cmd/evolve
    }
    $lines = Get-Content $path

    $heads = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^## (\d+)\. (.+)$') {
            $heads += [pscustomobject]@{ Num = [int]$Matches[1]; Title = $Matches[2]; Line = $i }
        }
    }

    if ($List) {
        Write-Host "`nSections`n" -ForegroundColor Cyan
        foreach ($h in $heads) { "{0,3}. {1}" -f $h.Num, $h.Title | Write-Host }
        return
    }

    if ($Section -gt 0) {
        $h = $heads | Where-Object { $_.Num -eq $Section }
        if (-not $h) { Write-Host "no section $Section (try -List)" -ForegroundColor Red; return }
        $next = $heads | Where-Object { $_.Num -eq ($Section + 1) }
        $end = if ($next) { $next.Line - 1 } else { $lines.Count - 1 }
        Write-Host ""
        for ($i = $h.Line; $i -le $end; $i++) {
            $l = $lines[$i]
            if ($l -match '^## ')                     { Write-Host $l -ForegroundColor Cyan }
            elseif ($l -match 'Expected \(written first\)') { Write-Host $l -ForegroundColor Yellow }
            elseif ($l -match 'CONTRADICTED')          { Write-Host $l -ForegroundColor Red }
            elseif ($l -match 'Found . HELD')          { Write-Host $l -ForegroundColor Green }
            else                                       { Write-Host $l }
        }
        return
    }

    # Default: the scoreboard, which is the honest summary of the whole thing.
    Write-Host "`n=== Zero-downtime schema evolution: prediction scoreboard ===`n" -ForegroundColor Cyan
    $inTally = $false
    foreach ($l in $lines) {
        if ($l -match '^\| # \| Section \| Status \|') { $inTally = $true }
        if (-not $inTally) { continue }
        if ($l -match 'CONTRADICTED') { Write-Host $l -ForegroundColor Red }
        elseif ($l -match 'HELD')     { Write-Host $l -ForegroundColor Green }
        else                          { Write-Host $l }
    }
    Write-Host "`nTry: ./demo.ps1 -Section 2   (the prediction that was wrong)" -ForegroundColor DarkGray
    Write-Host "     ./demo.ps1 -List`n" -ForegroundColor DarkGray
} finally {
    Pop-Location
}
