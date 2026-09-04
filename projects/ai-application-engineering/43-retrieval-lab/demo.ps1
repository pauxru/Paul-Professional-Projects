# Runs the retrieval lab and prints the report.
#
#   .\demo.ps1          run the experiment, print the report to the console
#   .\demo.ps1 -Save    run it and overwrite docs/results.md
#   .\demo.ps1 -Section 3a   print one section
#
# The elapsed time printed at the end is for the run, not for any comparison.
# No timing appears in the report itself -- cost there is counted operations,
# because a wall-clock number on a shared machine measures the machine
# (docs/adr/0004-counted-ops-not-wall-clock.md).

[CmdletBinding()]
param(
    [switch]$Save,
    [string]$Section
)

$ErrorActionPreference = "Stop"

$py = "C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe"
Set-Location $PSScriptRoot

$target = if ($Save) { "docs/results.md" } else { Join-Path $env:TEMP "rqlab-demo.md" }

Write-Host "Running the grid: 7 chunkers x 5 retrievers x 4 rerankers over 273 queries." -ForegroundColor Cyan
Write-Host "Everything is deterministic and offline. No API keys, no network.`n" -ForegroundColor DarkGray

$sw = [Diagnostics.Stopwatch]::StartNew()
& $py run_lab.py --out $target
if ($LASTEXITCODE -ne 0) { exit 1 }
$sw.Stop()

if ($Section) {
    $lines = Get-Content $target
    $start = ($lines | Select-String -Pattern "^## $([regex]::Escape($Section))\." | Select-Object -First 1).LineNumber
    if (-not $start) {
        $start = ($lines | Select-String -Pattern "^### $([regex]::Escape($Section))\." | Select-Object -First 1).LineNumber
    }
    if (-not $start) {
        Write-Host "no section '$Section'. Sections:" -ForegroundColor Red
        $lines | Select-String -Pattern "^###? \d" | ForEach-Object { Write-Host "  $($_.Line)" }
        exit 1
    }
    $rest = $lines[$start..($lines.Count - 1)]
    $end = ($rest | Select-String -Pattern "^###? \d" | Select-Object -First 1)
    $body = if ($end) { $rest[0..($end.LineNumber - 2)] } else { $rest }
    Write-Output $lines[$start - 1]
    $body | ForEach-Object { Write-Output $_ }
} elseif (-not $Save) {
    Get-Content $target | ForEach-Object { Write-Output $_ }
}

Write-Host "`n---" -ForegroundColor DarkGray
Write-Host ("run took {0:N1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
if ($Save) {
    Write-Host "wrote docs/results.md" -ForegroundColor Green
} else {
    Write-Host "docs/results.md was NOT modified (pass -Save to update it)" -ForegroundColor DarkGray
    Remove-Item $target -ErrorAction SilentlyContinue
}
