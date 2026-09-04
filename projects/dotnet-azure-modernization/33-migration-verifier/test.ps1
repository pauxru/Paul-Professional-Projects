# Six stages. Each one has to be able to fail for a reason that is not "the
# previous stage failed", which is why the mutation stage exists: it checks that
# the test suite can still detect a broken verifier. A verifier project whose own
# tests are decorative would be a poor advertisement.
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
if ($script:testCount -lt 100) { Fail "only $script:testCount tests ran; expected at least 100" }
Write-Host "  $script:testCount tests"

# The classpath is needed by the next two stages. Pinned plugin version in the
# pom; prefix resolution without one fails on this machine.
& $mvn -q dependency:build-classpath 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "could not resolve the classpath" }
$cp = "target\classes;" + (Get-Content target\classpath.txt -Raw).Trim()

# ---------------------------------------------------------------------------
Stage "results.md is current"

# The suite already byte-compares it against Experiments.run(). This checks the
# CLI path too -- encoding, line endings, the file-writing branch of Main.
$before = [IO.File]::ReadAllBytes("$PSScriptRoot\docs\results.md")
& $java '-Duser.timezone=UTC' -cp $cp dev.migver.Main | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "report generator exited non-zero" }
$after = [IO.File]::ReadAllBytes("$PSScriptRoot\docs\results.md")

if ($before.Length -ne $after.Length -or (Compare-Object $before $after -SyncWindow 0)) {
    Fail "docs/results.md changed when regenerated; commit the new version"
}
Write-Host "  $($after.Length) bytes, byte-identical after regeneration"

# ---------------------------------------------------------------------------
Stage "determinism"

# Two things could make this vary: HashMap iteration order leaking into the
# tables, and the JVM default timezone reaching the JDBC drivers. The second is
# a measured finding (P7), not a hypothetical, which is why the zone is pinned
# on the command line rather than only inside Experiments.run().
#
# NOTE on the -D flag being quoted: PowerShell's native argument parser splits
# -Duser.timezone=UTC into two tokens and java then tries to load
# ".timezone=UTC" as a class. Quoting it keeps it whole.
$hashes = @()
foreach ($i in 1..3) {
    $text = & $java '-Duser.timezone=UTC' -cp $cp dev.migver.Main --stdout
    if ($LASTEXITCODE -ne 0) { Fail "run $i exited non-zero" }
    if (-not $text) { Fail "run $i produced no output" }

    $joined = ($text -join "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($joined)
    $sha = [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '').Substring(0, 16).ToLower()
    $hashes += $sha
    Write-Host "  run $i  sha $sha  ($($joined.Length) chars)"
}

# sha256 of nothing at all. If three runs captured nothing they hash identically
# and this stage passes while verifying zero.
if ($hashes[0] -eq 'e3b0c44298fc1c14') { Fail "empty-input hash; the runs captured nothing" }
if (($hashes | Select-Object -Unique).Count -ne 1) { Fail "output differs between runs: $($hashes -join ', ')" }
Write-Host "  identical across 3 runs"

# ---------------------------------------------------------------------------
Stage "mutation: can the suite still detect a broken verifier?"

# Six single-token edits to the production sources. Each removes a real
# guarantee. If the suite still passes, the tests covering that guarantee are
# decorative.
$mutations = @(
    @{ File  = 'src\main\java\dev\migver\Rule.java'
       From  = 'case NUMERIC, BOOLEAN, IDENTIFIER -> true;'
       To    = 'case NUMERIC, BOOLEAN, IDENTIFIER -> false;'
       Kills = 'the injectivity claim stops matching the rules'' actual behaviour' }

    @{ File  = 'src\main\java\dev\migver\Rule.java'
       From  = 'case IDENTIFIER -> String.valueOf(v);'
       To    = 'case IDENTIFIER -> v;'
       Kills = 'the account rule stops reconciling String with Integer' }

    @{ File  = 'src\main\java\dev\migver\CutoverGate.java'
       From  = 'public static final double NOISE_CEILING = 0.05;'
       To    = 'public static final double NOISE_CEILING = 0.99;'
       Kills = 'an unusable verifier is allowed through the gate' }

    @{ File  = 'src\main\java\dev\migver\CutoverGate.java'
       From  = 'if (!Collections.disjoint(reported, planted)) {'
       To    = 'if (true) {'
       Kills = 'controls are credited without being attributed -- the ADR 005 bug' }

    @{ File  = 'src\main\java\dev\migver\Engine.java'
       From  = 'execute("SET IGNORECASE TRUE");'
       To    = 'execute("SET IGNORECASE FALSE");'
       Kills = 'the source stops being case-insensitive, removing the collation hazard' }

    @{ File  = 'src\main\java\dev\migver\Confusion.java'
       From  = 'return d == 0 ? 1.0 : (double) tp() / d;'
       To    = 'return d == 0 ? 1.0 : (double) fp() / d;'
       Kills = 'every precision figure in the report inverts' }
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
& $java '-Duser.timezone=UTC' -cp $cp dev.migver.Main | Out-Null

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
