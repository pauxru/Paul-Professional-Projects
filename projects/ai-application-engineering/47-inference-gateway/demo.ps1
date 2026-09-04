# The headline experiment, in about a minute: is a latency problem a capacity
# problem or a queueing problem?
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host ""
Write-Host "Building (release -- the debug build is roughly 10x slower)..." -ForegroundColor Cyan
& .\cargo.ps1 build --offline --release --quiet 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "build failed" -ForegroundColor Red; exit 1 }

Write-Host "Running twelve experiments..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& .\cargo.ps1 run --offline --release --quiet --bin run_gateway 2>&1 | Out-Null
$sw.Stop()
if (-not (Test-Path docs\results.md)) { Write-Host "no report produced" -ForegroundColor Red; exit 1 }

$report = Get-Content docs\results.md -Raw
Write-Host ("Done in {0:N1}s. Full report: docs\results.md" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host ""

$scoreboard = ($report -split "`n" | Select-String 'Predictions registered').Line
Write-Host $scoreboard.Replace('**','') -ForegroundColor Yellow
Write-Host ""

# Section 4: the utilisation curve. The shape is the entire argument.
Write-Host "--- Utilisation against latency (section 4) -----------------------" -ForegroundColor Cyan
$section4 = ($report -split '## 4\.')[1] -split '## 5\.'
$section4[0] -split "`n" | Where-Object { $_ -match '^\| [0-9]' -or $_ -match '^\| utilisation' -or $_ -match '^\|---' } | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "  TPOT barely moves across the whole sweep. All the damage lands in TTFT," -ForegroundColor DarkGray
Write-Host "  because that is where waiting lives. A dashboard tracking tokens per" -ForegroundColor DarkGray
Write-Host "  second shows this system getting better right up to the point users leave." -ForegroundColor DarkGray
Write-Host ""

# Section 9: the decision the whole thing exists for.
Write-Host "--- Buying capacity against refusing work (section 9) -------------" -ForegroundColor Cyan
$section9 = ($report -split '## 9\. ')[1] -split '### 9a'
$section9[0] -split "`n" | Where-Object { $_ -match '^\| ' } | ForEach-Object { Write-Host "  $_" }
Write-Host ""

$marginal = ($report -split '### 9a\.')[1] -split '### 9b'
($marginal[0] -split "`n" | Where-Object { $_ -match '^\*\*' }) | ForEach-Object {
    Write-Host ("  " + $_.Replace('**','')) -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Read next:" -ForegroundColor Cyan
Write-Host "  docs\results.md                                  all twelve experiments"
Write-Host "  docs\portfolio\04-bugs-the-simulator-found.md    six bugs, five of which"
Write-Host "                                                   produced plausible output"
Write-Host ""
