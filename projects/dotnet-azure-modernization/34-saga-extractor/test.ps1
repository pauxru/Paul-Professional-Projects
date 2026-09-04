# Six stages. Each one has to be able to fail for a reason that is not "the
# previous stage failed", which is why the mutation stage exists: it checks that
# the test suite can still detect a broken checker.
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

$sw = [Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------
Stage "build with warnings as errors"

dotnet build -c Release --nologo -v q -warnaserror 2>&1 | Where-Object { $_ -match '\S' }
if ($LASTEXITCODE -ne 0) { Fail "build produced warnings or errors" }
Write-Host "  clean"

# ---------------------------------------------------------------------------
Stage "tests"

$out = dotnet test -c Release --nologo -v q --no-build 2>&1
$out | Where-Object { $_ -match 'Passed!|Failed!|error' }
if ($LASTEXITCODE -ne 0) { Fail "test suite is red" }

$line = $out | Where-Object { $_ -match 'Passed:\s+(\d+)' } | Select-Object -First 1
if ($line -match 'Passed:\s+(\d+)') { $script:testCount = [int]$Matches[1] }
if ($script:testCount -lt 80) { Fail "only $script:testCount tests ran; expected at least 80" }
Write-Host "  $script:testCount tests"

# ---------------------------------------------------------------------------
Stage "results.md is current"

# The test suite already byte-compares it, but only against Experiments.Render().
# This checks the CLI path too -- encoding, line endings, the lot.
$before = [IO.File]::ReadAllBytes("$PSScriptRoot\docs\results.md")
dotnet run --project src\Sagas.Report -c Release --no-build -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "report generator exited non-zero" }
$after = [IO.File]::ReadAllBytes("$PSScriptRoot\docs\results.md")

if ($before.Length -ne $after.Length -or (Compare-Object $before $after -SyncWindow 0)) {
    Fail "docs/results.md changed when regenerated; commit the new version"
}
Write-Host "  $($after.Length) bytes, byte-identical after regeneration"

# ---------------------------------------------------------------------------
Stage "determinism"

# NOTE: the '--' separator is quoted. PowerShell's parameter binder eats a bare
# '--' before the target ever sees it, and the failure is silent: the command
# exits non-zero, captures nothing, and three empty strings hash identically. A
# sha of e3b0c442... is the fingerprint of a check that verified nothing.
$hashes = @()
foreach ($i in 1..3) {
    $text = dotnet run --project src\Sagas.Report -c Release --no-build -v q --nologo '--' --stdout
    if ($LASTEXITCODE -ne 0) { Fail "run $i exited non-zero" }
    if (-not $text) { Fail "run $i produced no output" }

    $joined = ($text -join "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($joined)
    $sha = [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '').Substring(0, 16).ToLower()
    $hashes += $sha
    Write-Host "  run $i  sha $sha  ($($joined.Length) chars)"
}

if ($hashes[0] -eq 'e3b0c44298fc1c14') { Fail "empty-input hash; the runs captured nothing" }
if (($hashes | Select-Object -Unique).Count -ne 1) { Fail "output differs between runs: $($hashes -join ', ')" }
Write-Host "  identical across 3 runs"

# ---------------------------------------------------------------------------
Stage "mutation: can the suite still detect a broken checker?"

# Five single-token edits to the production sources. Each one removes a real
# guarantee. If the suite still passes, the tests covering that guarantee are
# decorative.
$mutations = @(
    @{ File = 'src\Sagas.Core\Checker.cs'
       From = 'if (UndeclaredResidue(saga, s).Count == 0)'
       To   = 'if (true)'
       Kills = 'the algebraic reverse-order check never fires' }

    @{ File = 'src\Sagas.Core\Checker.cs'
       From = 'Violations = [.. staticViolations, .. violations],'
       To   = 'Violations = [.. violations],'
       Kills = 'shape violations are silently dropped' }

    @{ File = 'src\Sagas.Core\Extractor.cs'
       From = 'Reversibility.ExternallyVisible => 1,'
       To   = 'Reversibility.ExternallyVisible => 3,'
       Kills = 'un-undoable work is scheduled before the pivot' }

    @{ File = 'src\Sagas.Core\Saga.cs'
       From = 'public int MaxAttempts { get; init; } = 2;'
       To   = 'public int MaxAttempts { get; init; } = 1;'
       Kills = 'retries are removed, changing every measured number' }

    @{ File = 'src\Sagas.Core\Report.cs'
       From = 'RequireClosed("rendering the document");'
       To   = ''
       Kills = 'a report can be rendered with an unresolved prediction' }
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
        dotnet test -c Release --nologo -v q 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
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
    dotnet build -c Release --nologo -v q | Out-Null
    Fail "$($mutations.Count - $killed) of $($mutations.Count) mutations survived"
}
Write-Host "  $killed/$($mutations.Count) killed"

# Restore the build output the mutations churned.
dotnet build -c Release --nologo -v q | Out-Null

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
