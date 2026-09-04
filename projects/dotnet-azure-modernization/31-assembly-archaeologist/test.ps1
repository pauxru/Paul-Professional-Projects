# Six stages. The mutation stage is the one that matters: a project whose entire
# argument is "the graph you can compute is not the graph you must migrate" cannot
# ship a suite whose sensitivity nobody has checked.
#
# Exits non-zero on the first failure.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$script:stage = 0
function Stage($name) {
    $script:stage++
    Write-Host ""
    Write-Host ("=" * 78)
    Write-Host ("  stage $script:stage -- $name")
    Write-Host ("=" * 78)
}

function Fail($msg) {
    Write-Host ""
    Write-Host "FAILED: $msg" -ForegroundColor Red
    exit 1
}

function RunTests {
    $out = & dotnet test -v q --nologo 2>&1
    return @{ Ok = ($LASTEXITCODE -eq 0); Out = $out }
}

$sw = [Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------
Stage "build"

$out = & dotnet build -c Release --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) {
    $out | Where-Object { $_ -match 'error|warning' } | ForEach-Object { Write-Host "  $_" }
    Fail "build failed"
}
$warnings = $out | Select-String -Pattern ': warning '
if ($warnings) {
    $warnings | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Yellow }
    Fail "$($warnings.Count) compiler warning(s)"
}
Write-Host "  clean, no warnings"

# ---------------------------------------------------------------------------
Stage "tests"

$out = & dotnet test --nologo 2>&1
$summary = $out | Select-String -Pattern 'Passed!.*Total:\s*\d+' | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) {
    $out | Select-String -Pattern '\[FAIL\]|Error Message' | ForEach-Object { Write-Host "  $($_.Line)" }
    Fail "test suite is red"
}
if ("$summary" -match 'Total:\s*(\d+)') { $script:testCount = [int]$Matches[1] }
if ($script:testCount -lt 200) { Fail "only $script:testCount tests ran; expected at least 200" }
Write-Host "  $script:testCount tests"

# ---------------------------------------------------------------------------
Stage "results.md is current"

# The suite already byte-compares against Experiments.Run(). This exercises the
# CLI path too: encoding, line endings, repo-root discovery, the file branch.
$before = [IO.File]::ReadAllBytes("$PSScriptRoot\docs\results.md")
& dotnet run --project Archaeologist.Cli -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "report generator exited non-zero" }
$after = [IO.File]::ReadAllBytes("$PSScriptRoot\docs\results.md")

if ($before.Length -ne $after.Length -or (Compare-Object $before $after -SyncWindow 0)) {
    Fail "docs/results.md changed when regenerated; commit the new version"
}
Write-Host "  $($after.Length) bytes, byte-identical after regeneration"

# ---------------------------------------------------------------------------
Stage "determinism"

# Cecil writes MVIDs and the analyser walks Dictionary instances. Anything that
# leaks an unordered iteration into a rendered table shows up here as a flaky
# test rather than as a wrong number in front of a client. Three fresh processes,
# hashed -- not three calls inside one process, which would share every cache.
$hashes = @()
foreach ($i in 1..3) {
    $text = & dotnet run --project Archaeologist.Cli -c Release -v q --nologo -- '--stdout'
    if ($LASTEXITCODE -ne 0) { Fail "run $i exited non-zero" }
    if (-not $text) { Fail "run $i produced no output" }

    $joined = ($text -join "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($joined)
    $sha = [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '').Substring(0, 16).ToLower()
    $hashes += $sha
    Write-Host "  run $i  sha $sha  ($($joined.Length) chars)"
}

# sha256 of nothing at all. Three runs that captured nothing hash identically and
# would sail through this stage having verified precisely zero.
if ($hashes[0] -eq 'e3b0c44298fc1c14') { Fail "empty-input hash; the runs captured nothing" }
if (($hashes | Select-Object -Unique).Count -ne 1) { Fail "output differs between runs: $($hashes -join ', ')" }
Write-Host "  identical across 3 runs"

# ---------------------------------------------------------------------------
Stage "mutation: can the suite still detect a broken analysis?"

# Seven single-token edits, each removing a guarantee some number in results.md
# depends on. A survivor means that number is unverified.
#
# One candidate was tried and dropped: replacing the ordinal comparer in
# DiGraph's SortedDictionary with the default string comparer. It survives on
# this corpus, because every identifier in it is ASCII and invariant ordering
# agrees with ordinal on ASCII. That is a latent portability bug rather than a
# hole in the suite -- it would bite on a corpus with a Turkish 'I' in a type
# name, which this one has no reason to contain. Recorded in known-limitations
# rather than papered over with a test that pretends to catch it.
$mutations = @(
    @{ File  = 'Archaeologist.Core\IlReader.cs'
       From  = 'counts[k] = counts.GetValueOrDefault(k) + e.Weight;'
       To    = 'counts[k] = counts.GetValueOrDefault(k) + 1;'
       Kills = 'cut cost stops counting call sites, so "what would this cost" loses its unit' }

    @{ File  = 'Archaeologist.Core\BlockerRules.cs'
       From  = 'var specific = candidates.FirstOrDefault(r => r.MemberName == memberName);'
       To    = 'var specific = candidates.FirstOrDefault(r => false);'
       Kills = 'member precision collapses to type precision -- the argument against reference counting' }

    @{ File  = 'Archaeologist.Core\Graphs.cs'
       From  = 'else if (onStack.Contains(w))'
       To    = 'else if (index.ContainsKey(w))'
       Kills = 'Tarjan merges components across a cross-edge and invents entanglement that is not there' }

    @{ File  = 'Archaeologist.Core\FeedbackArcSet.cs'
       From  = '        if (n > maxNodes)'
       To    = '        if (n > int.MaxValue)'
       Kills = 'the exact solver stops refusing and starts taking longer than the migration' }

    @{ File  = 'Archaeologist.Core\Reachability.cs'
       From  = 'if (mode == ReachabilityMode.DecidableReflection) continue;'
       To    = 'if (mode != ReachabilityMode.DecidableReflection) continue;'
       Kills = 'the soundness lattice inverts and the strictest analysis deletes the most code' }

    @{ File  = 'Archaeologist.Core\CycleDissolver.cs'
       From  = 'if (a != b) edges.Add((a, b));'
       To    = 'if (a != b && a != Extracted) edges.Add((a, b));'
       Kills = 'extraction advice ignores edges into the new assembly, so "move one type" is a guess' }

    @{ File  = 'Archaeologist.Core\Report.cs'
       From  = 'if (unsettled.Count > 0)'
       To    = 'if (unsettled.Count > 99)'
       Kills = 'a prediction that came out badly can be quietly dropped from the report' }
)

$killed = 0
foreach ($m in $mutations) {
    $path = Join-Path $PSScriptRoot $m.File
    $original = [IO.File]::ReadAllText($path)

    if (-not $original.Contains($m.From)) {
        Fail "mutation target not found in $($m.File): $($m.From)"
    }

    [IO.File]::WriteAllText($path, $original.Replace($m.From, $m.To))
    try {
        $r = RunTests
        if ($r.Ok) {
            Write-Host "  SURVIVED  $($m.Kills)" -ForegroundColor Red
        }
        else {
            $killed++
            Write-Host "  killed    $($m.Kills)"
        }
    }
    finally {
        [IO.File]::WriteAllText($path, $original)
    }
}

if ($killed -ne $mutations.Count) {
    & dotnet build -c Release --nologo -v q 2>&1 | Out-Null
    Fail "$($mutations.Count - $killed) of $($mutations.Count) mutations survived"
}
Write-Host "  $killed/$($mutations.Count) killed"

# Rebuild from restored sources, and regenerate the report the mutants churned.
& dotnet build -c Release --nologo -v q 2>&1 | Out-Null
& dotnet run --project Archaeologist.Cli -c Release -v q --nologo | Out-Null

# ---------------------------------------------------------------------------
Stage "secrets"

$patterns = @(
    'AKIA[0-9A-Z]{16}',
    'ghp_[A-Za-z0-9]{36}',
    'sk-[A-Za-z0-9]{32,}',
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    'password\s*=\s*["''][^"'']{4,}["'']'
)

$files = Get-ChildItem -Recurse -File -Include *.cs, *.md, *.ps1, *.csproj, *.slnx |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' }

$found = @()
foreach ($p in $patterns) {
    $found += $files | Select-String -Pattern $p -AllMatches
}

if ($found.Count -gt 0) {
    $found | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)" -ForegroundColor Red }
    Fail "$($found.Count) possible secret(s)"
}
Write-Host "  $($files.Count) files scanned, nothing found"

# ---------------------------------------------------------------------------
$sw.Stop()
Write-Host ""
Write-Host ("=" * 78)
Write-Host ("  all 6 stages passed -- $script:testCount tests, $killed/$($mutations.Count) mutations killed, {0:N1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host ("=" * 78)
exit 0
