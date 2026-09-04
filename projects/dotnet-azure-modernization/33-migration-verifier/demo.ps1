# Regenerates docs/results.md from the code and prints the headline numbers.
#
# Everything printed here is produced by running against real H2 and SQLite JDBC
# drivers. Nothing is hard-coded.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $env:JAVA_HOME) { $env:JAVA_HOME = 'C:\Users\rukwaropaul\toolchains\jdk' }
$mvn  = 'C:\Users\rukwaropaul\toolchains\maven\bin\mvn.cmd'
$java = Join-Path $env:JAVA_HOME 'bin\java.exe'

function Rule($t) {
    Write-Host ""
    Write-Host ("-" * 78) -ForegroundColor DarkGray
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ("-" * 78) -ForegroundColor DarkGray
}

Rule "building"
& $mvn -q compile 2>&1 | Where-Object { $_ -match '\S' }
if ($LASTEXITCODE -ne 0) { Write-Host "compile failed" -ForegroundColor Red; exit 1 }
& $mvn -q dependency:build-classpath 2>&1 | Out-Null
$cp = "target\classes;" + (Get-Content target\classpath.txt -Raw).Trim()
Write-Host "  ok"

Rule "generating docs/results.md"
& $java '-Duser.timezone=UTC' -cp $cp dev.migver.Main
if ($LASTEXITCODE -ne 0) { Write-Host "generator failed" -ForegroundColor Red; exit 1 }

$md = Get-Content docs\results.md

Rule "a faithful migration, row 1 -- nothing went wrong"
$i = ($md | Select-String -Pattern '^id\s+source').LineNumber | Select-Object -First 1
$md[($i - 1)..($i + 5)] | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "  three of seven columns differ, on every row" -ForegroundColor Yellow

Rule "the rule-subset lattice -- 128 subsets, two outcomes"
($md | Select-String -Pattern 'subsets? (of the seven|false-flag|flag none)|no gradient' |
    Select-Object -First 3) | ForEach-Object { Write-Host "  $($_.Line)" }

Rule "the cutover gate"
$g = ($md | Select-String -Pattern '^\| `(row-count|naive-checksum|canonicalising)').LineNumber
if ($g) {
    $start = ($g | Select-Object -Last 6)[0]
    $md[($start - 3)..($start + 5)] | ForEach-Object { Write-Host "  $_" }
}

Rule "predictions"
$held = ($md | Select-String -Pattern '^> \*\*Held').Count
$contra = ($md | Select-String -Pattern '^> \*\*Contradicted').Count
Write-Host "  $held held, $contra contradicted, out of $($held + $contra) registered before the runs"
Write-Host ""
Write-Host "  the contradicted ones are the point; see docs/results.md" -ForegroundColor Yellow

Write-Host ""
Write-Host ("=" * 78)
Write-Host "  full report: docs\results.md    ($((Get-Item docs\results.md).Length) bytes)"
Write-Host "  the idea:    docs\portfolio\03-the-injectivity-criterion.md"
Write-Host "  verify:      .\test.ps1"
Write-Host ("=" * 78)
