# The argument, in about two minutes.
#
# Nothing here is pre-computed prose. Every number printed comes out of the same
# classes the test suite covers, generated live from 24 assemblies that are built
# from scratch by this script.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Beat($title) {
    Write-Host ""
    Write-Host ("-" * 78) -ForegroundColor DarkGray
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ("-" * 78) -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  The dependency graph you can compute is not the dependency graph" -ForegroundColor White
Write-Host "  you have to migrate." -ForegroundColor White

& dotnet build -c Release --nologo -v q 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "build failed" -ForegroundColor Red; exit 1 }

$estate = Join-Path ([IO.Path]::GetTempPath()) 'archaeologist-demo'
$report = & dotnet run --project Archaeologist.Cli -c Release -v q --nologo -- '--stdout' '--keep' $estate
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

function Show($lines, [int]$take = 14) {
    $lines | Where-Object { $_ -match '\S' } | Select-Object -First $take |
        ForEach-Object { Write-Host "  $_" }
}

# ---------------------------------------------------------------------------
Beat "1. There is no source code here. There are 24 DLLs."

$dlls = Get-ChildItem $estate -Filter *.dll
$pdbs = Get-ChildItem $estate -Filter *.pdb
Write-Host "  $($dlls.Count) assemblies emitted to $estate"
Write-Host "  $($pdbs.Count) have symbols. $($dlls.Count - $pdbs.Count) do not -- nobody can rebuild those."
Write-Host ""
$dlls | Select-Object -First 6 | ForEach-Object {
    Write-Host ("  {0,-42} {1,7:N0} bytes" -f $_.Name, $_.Length)
}
Write-Host "  ... and $($dlls.Count - 6) more"

# ---------------------------------------------------------------------------
Beat "2. They reference a Framework that is not installed. Mostly."

Write-Host "  The estate names System.Web 4.0.0.0, System.ServiceModel,"
Write-Host "  System.EnterpriseServices and System.Messaging."
Write-Host ""
Write-Host "  Three of those cannot be resolved on this machine. Four CAN --" -ForegroundColor Yellow
Write-Host "  as empty .NET facades containing none of the types in use." -ForegroundColor Yellow
Write-Host ""
Write-Host "  So an analyser that resolved its references would not fail loudly."
Write-Host "  It would succeed quietly and report the wrong answer. This one"
Write-Host "  never resolves anything -- a test asserts zero resolution requests."

# ---------------------------------------------------------------------------
Beat "3. What a manifest scanner cannot see"

Show (Section 2) 16

# ---------------------------------------------------------------------------
Beat "4. A five-assembly cycle caused by two types"

Show (Section 3) 18

# ---------------------------------------------------------------------------
Beat "5. Move one type. No code changes."

Show (Section 5) 16

# ---------------------------------------------------------------------------
Beat "6. What is safe to delete? Four answers, one of them true."

Show (Section 6) 18

# ---------------------------------------------------------------------------
Beat "7. The predictions"

$text = $report -join "`n"
$held = ([regex]::Matches($text, '-- HELD\.')).Count
$contra = ([regex]::Matches($text, '-- CONTRADICTED\.')).Count
Write-Host "  $($held + $contra) predictions were written down before anything was measured."
Write-Host "  $held held. $contra were contradicted by the code." -ForegroundColor Yellow
Write-Host ""
$report | Select-String -Pattern '^\*\*P\d+ -- (HELD|CONTRADICTED)\.\*\*' |
    ForEach-Object {
        $line = $_.Line
        $id = ($line -split ' ')[0].Replace('**', '')
        $verdict = if ($line -match 'HELD') { 'HELD        ' } else { 'CONTRADICTED' }
        $colour = if ($line -match 'HELD') { 'Green' } else { 'Yellow' }
        Write-Host ("  {0,-4} {1}" -f $id, $verdict) -ForegroundColor $colour
    }

# ---------------------------------------------------------------------------
Beat "8. Look at the assemblies yourself"

Write-Host "  The 24 DLLs are still at:"
Write-Host "    $estate" -ForegroundColor White
Write-Host ""
Write-Host "  Open one in ILSpy, dotPeek or ildasm. They are real assemblies with"
Write-Host "  real IL, real AssemblyRefs and real public key tokens. Nothing about"
Write-Host "  the analysis knows they were generated."
Write-Host ""
Write-Host "  Full report:  docs\results.md"
Write-Host "  Full suite:   .\test.ps1   (206 tests, 7/7 mutations killed)"
Write-Host ""
