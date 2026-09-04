# The argument, in about 90 seconds.
#
# Runs the real code -- nothing here is pre-computed prose. Every number printed
# is produced by the same classes the test suite covers.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $env:JAVA_HOME) { $env:JAVA_HOME = 'C:\Users\rukwaropaul\toolchains\jdk' }
$mvn  = 'C:\Users\rukwaropaul\toolchains\maven\bin\mvn.cmd'
$java = Join-Path $env:JAVA_HOME 'bin\java.exe'
if (-not (Test-Path $java)) { Write-Host "no JDK at $env:JAVA_HOME" -ForegroundColor Red; exit 1 }

function Beat($title) {
    Write-Host ""
    Write-Host ("-" * 78) -ForegroundColor DarkGray
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ("-" * 78) -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  Graph traversal and vector retrieval are not competing answers" -ForegroundColor White
Write-Host "  to one question." -ForegroundColor White

& $mvn -q compile 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "compile failed" -ForegroundColor Red; exit 1 }
& $mvn -q dependency:build-classpath 2>&1 | Out-Null
$cp = "target\classes;" + (Get-Content target\classpath.txt -Raw).Trim()

$report = & $java '-Duser.timezone=UTC' -cp $cp dev.hybrid.Main '--stdout'
if ($LASTEXITCODE -ne 0) { Write-Host "report generation failed" -ForegroundColor Red; exit 1 }

function Section([int]$n) {
    $text = $report -join "`n"
    $start = $text.IndexOf("`n## $n. ")
    if ($start -lt 0) { return @() }
    $rest = $text.Substring($start + 1)
    $end = $rest.IndexOf("`n## ")
    if ($end -ge 0) { $rest = $rest.Substring(0, $end) }
    return $rest -split "`n"
}

# ---------------------------------------------------------------------------
Beat "1. The fact that is not written down"

Write-Host ""
Write-Host "  Five documents from the corpus:" -ForegroundColor DarkGray
Section 1 | Where-Object { $_ -match '^\| d\d' } | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "  Composed, they say Ashford Components buys from a company controlled"
Write-Host "  by a sanctioned person. No document says that. No filing would."

# ---------------------------------------------------------------------------
Beat "2. Retrieval does not degrade on composition. It falls off a cliff."

Section 2 | Where-Object { $_ -match '^\|' } | ForEach-Object { Write-Host "  $_" }

# ---------------------------------------------------------------------------
Beat "3. Raising k does not fix it"

Write-Host ""
Write-Host "  Fraction of a question's premise documents inside the top k:" -ForegroundColor DarkGray
Section 3 | Where-Object { $_ -match '^\|' } | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "  The premises that stay missing are the middle links -- the ones with no"
Write-Host "  lexical or semantic relationship to the question that needs them."

# ---------------------------------------------------------------------------
Beat "4. Proof that the cause is retrieval, not reasoning"

Section 4 | Where-Object { $_ -match '^> \*\*Held' } | ForEach-Object {
    Write-Host "  $_" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
Beat "5. But the graph is built by an embedding"

Write-Host ""
Write-Host "  Sweeping the entity-resolution threshold:" -ForegroundColor DarkGray
Section 5 | Where-Object { $_ -match '^\|' } | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "  Splits fail by omission. Merges fail by invention." -ForegroundColor Yellow
Write-Host "  A missing answer gets escalated. A fabricated one gets acted on."

# ---------------------------------------------------------------------------
Beat "6. Where the graph loses -- and it is not paraphrase"

Section 6 | Where-Object { $_ -match '^\| Q' } | ForEach-Object { Write-Host "  $_" }

# ---------------------------------------------------------------------------
Beat "9. Provenance, and what it is not evidence of"

$text = $report -join "`n"
$s = $text.IndexOf('```')
if ($s -ge 0) {
    $e = $text.IndexOf('```', $s + 3)
    if ($e -gt $s) {
        ($text.Substring($s + 3, $e - $s - 3) -split "`n") |
            Where-Object { $_ -match '\S' } | ForEach-Object { Write-Host "  $_" }
    }
}
Write-Host ""
Write-Host "  Every one of those documents is real and states the relation claimed."
Write-Host "  So did the fabricated path in section 5." -ForegroundColor Yellow
Write-Host ""
Write-Host "  A provenance chain is evidence that the edges were asserted."
Write-Host "  It is not evidence that the entities were correctly resolved."

# ---------------------------------------------------------------------------
Beat "The scoreboard"

Write-Host ""
$report | Where-Object { $_ -match 'predictions registered before measurement' } |
    ForEach-Object { Write-Host "  $_" -ForegroundColor White }
Write-Host ""
Write-Host "  Every prediction was written into the code before the measurement that"
Write-Host "  settles it. Report.render() refuses to emit a document with an open one."

Write-Host ""
Write-Host ("=" * 78)
Write-Host "  Retrieval finds text. Traversal derives facts." -ForegroundColor Green
Write-Host "  Vector for identity, graph for traversal -- and the failure mode of the" -ForegroundColor Green
Write-Host "  combination is a well-cited path between the wrong nodes." -ForegroundColor Green
Write-Host ("=" * 78)
Write-Host ""
Write-Host "  Full report: docs/results.md      Verify: .\test.ps1" -ForegroundColor DarkGray
Write-Host ""
exit 0
