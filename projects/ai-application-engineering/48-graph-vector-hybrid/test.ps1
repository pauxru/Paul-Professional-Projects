# Six stages. Each has to be able to fail for a reason that is not "the previous
# stage failed", which is why the mutation stage exists: a project whose whole
# argument is "measure it, don't assume it" cannot ship a test suite nobody has
# checked the sensitivity of.
#
# Exits non-zero on the first failure.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $env:JAVA_HOME) { $env:JAVA_HOME = 'C:\Users\rukwaropaul\toolchains\jdk' }
$mvn  = 'C:\Users\rukwaropaul\toolchains\maven\bin\mvn.cmd'
$java = Join-Path $env:JAVA_HOME 'bin\java.exe'
if (-not (Test-Path $java)) { Write-Host "no JDK at $env:JAVA_HOME" -ForegroundColor Red; exit 1 }

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
    $out = & $mvn -q test 2>&1
    return @{ Ok = ($LASTEXITCODE -eq 0); Out = $out }
}

$sw = [Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------
Stage "compile"

& $mvn -q -DcompilerArgument=-Xlint:all clean compile 2>&1 | Where-Object { $_ -match '\S' }
if ($LASTEXITCODE -ne 0) { Fail "compile failed" }
Write-Host "  clean"

# ---------------------------------------------------------------------------
Stage "tests"

$out = & $mvn test 2>&1
$summary = $out | Select-String -Pattern 'Tests run:.*Failures' | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) {
    $out | Select-String -Pattern 'ERROR\]   ' | ForEach-Object { Write-Host "  $($_.Line)" }
    Fail "test suite is red"
}
if ("$summary" -match 'Tests run:\s*(\d+)') { $script:testCount = [int]$Matches[1] }
if ($script:testCount -lt 120) { Fail "only $script:testCount tests ran; expected at least 120" }
Write-Host "  $script:testCount tests"

& $mvn -q dependency:build-classpath 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "could not resolve the classpath" }
$cp = "target\classes;" + (Get-Content target\classpath.txt -Raw).Trim()

# ---------------------------------------------------------------------------
Stage "results.md is current"

# The suite already byte-compares it against Experiments.run(). This checks the
# CLI path as well -- encoding, line endings, the file-writing branch of Main.
$before = [IO.File]::ReadAllBytes("$PSScriptRoot\docs\results.md")
& $java '-Duser.timezone=UTC' -cp $cp dev.hybrid.Main | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "report generator exited non-zero" }
$after = [IO.File]::ReadAllBytes("$PSScriptRoot\docs\results.md")

if ($before.Length -ne $after.Length -or (Compare-Object $before $after -SyncWindow 0)) {
    Fail "docs/results.md changed when regenerated; commit the new version"
}
Write-Host "  $($after.Length) bytes, byte-identical after regeneration"

# ---------------------------------------------------------------------------
Stage "determinism"

# The realistic sources of drift here are HashMap and Set.of() iteration order
# reaching a rendered table. Set.of() is salted per JVM, so an unordered
# collection whose order escapes into the report is a coin flip that presents as
# a flaky test -- see project 33, where exactly that cost an afternoon. Three
# separate JVMs, hashed.
#
# NOTE on the quoted -D flag: PowerShell's native argument parser splits
# -Duser.timezone=UTC into two tokens and java then tries to load
# ".timezone=UTC" as a class. Quoting keeps it whole.
$hashes = @()
foreach ($i in 1..3) {
    $text = & $java '-Duser.timezone=UTC' -cp $cp dev.hybrid.Main '--stdout'
    if ($LASTEXITCODE -ne 0) { Fail "run $i exited non-zero" }
    if (-not $text) { Fail "run $i produced no output" }

    $joined = ($text -join "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($joined)
    $sha = [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '').Substring(0, 16).ToLower()
    $hashes += $sha
    Write-Host "  run $i  sha $sha  ($($joined.Length) chars)"
}

# sha256 of nothing at all. Three runs that captured nothing hash identically
# and would pass this stage while verifying zero.
if ($hashes[0] -eq 'e3b0c44298fc1c14') { Fail "empty-input hash; the runs captured nothing" }
if (($hashes | Select-Object -Unique).Count -ne 1) { Fail "output differs between runs: $($hashes -join ', ')" }
Write-Host "  identical across 3 runs"

# ---------------------------------------------------------------------------
Stage "mutation: can the suite still detect a broken comparison?"

# Six single-token edits, each removing a guarantee the report depends on. If
# the suite still passes, the tests covering that guarantee are decorative and
# the corresponding number in results.md is unverified.
#
# One candidate was tried and dropped: replacing the tie-break comparator in
# Retriever.topK with `return c;`. It survives, and it survives for a good
# reason -- List.sort is a stable TimSort and the candidate list is built in
# ascending id order, so the tie-break is provably unobservable. That is an
# equivalent mutant, not a hole in the suite. Recording it here rather than
# quietly deleting it, because "the mutation survived" and "the mutation could
# not possibly have changed anything" are different findings and only one of
# them is a problem.
$mutations = @(
    @{ File  = 'src\main\java\dev\hybrid\Retriever.java'
       From  = 'scores[i] = Embedding.cosine(q, vectors.get(i));'
       To    = 'scores[i] = -Embedding.cosine(q, vectors.get(i));'
       Kills = 'the ranking inverts and the least relevant document is retrieved first' }

    @{ File  = 'src\main\java\dev\hybrid\Graph.java'
       From  = 'if (direction != Direction.BACKWARD) {'
       To    = 'if (true) {'
       Kills = 'traversal stops respecting edge direction -- "who owns X" answers "what X owns"' }

    @{ File  = 'src\main\java\dev\hybrid\Executor.java'
       From  = 'if (plan.guard() != null && !satisfies(p.target(), plan.guard(), graph)) {'
       To    = 'if (false) {'
       Kills = 'the guard stops filtering, so every supplier looks sanctioned' }

    @{ File  = 'src\main\java\dev\hybrid\Plan.java'
       From  = 'return new Plan(f.apply(start), steps, g, count);'
       To    = 'return new Plan(start, steps, g, count);'
       Kills = 'the query entity skips resolution, so section 5 measures a naming accident' }

    @{ File  = 'src\main\java\dev\hybrid\Resolver.java'
       From  = 'canonicalOrder.clear();'
       To    = 'canonicalOrder.size();'
       Kills = 'resolve() becomes stateful again -- the same input gives two answers' }

    @{ File  = 'src\main\java\dev\hybrid\Score.java'
       From  = 'return goldEmpty && !predictedEmpty;'
       To    = 'return false;'
       Kills = 'false alarms stop being counted, and section 5''s whole asymmetry disappears' }
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
    & $mvn -q clean compile 2>&1 | Out-Null
    Fail "$($mutations.Count - $killed) of $($mutations.Count) mutations survived"
}
Write-Host "  $killed/$($mutations.Count) killed"

# Restore the class files the mutations churned, and the report with them.
& $mvn -q clean compile 2>&1 | Out-Null
& $java '-Duser.timezone=UTC' -cp $cp dev.hybrid.Main | Out-Null

# ---------------------------------------------------------------------------
Stage "secrets"

$patterns = @(
    'AKIA[0-9A-Z]{16}',
    'ghp_[A-Za-z0-9]{36}',
    'sk-[A-Za-z0-9]{32,}',
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    'password\s*=\s*["''][^"'']{4,}["'']'
)

$files = Get-ChildItem -Recurse -File -Include *.java, *.md, *.ps1, *.xml |
    Where-Object { $_.FullName -notmatch '\\(target|\.git)\\' }

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
