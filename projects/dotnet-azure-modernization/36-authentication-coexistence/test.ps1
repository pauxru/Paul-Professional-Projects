# Six stages.
#
# Stage 4 is the one that justifies the project. Everything else checks that the code
# does what it says; stage 4 checks that the *report* does, by regenerating it and
# demanding the claims come out byte-identical. A project whose entire output is a set
# of measurements cannot ship measurements nobody re-ran.
#
# Stage 5 asks the harder question: if the checks the report's headlines depend on
# were quietly removed, would anything here notice? Six single-token edits, each one
# deleting exactly one guarantee the document claims to have measured.
#
# Assumes build.ps1 has already run. Exits non-zero on the first failure.

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

function RunTests($filter) {
    $a = @('test', 'Auth.Tests\Auth.Tests.csproj', '-c', 'Release', '--nologo', '-v', 'q')
    if ($filter) { $a += @('--filter', $filter) }
    $out = & dotnet @a 2>&1
    return @{ Ok = ($LASTEXITCODE -eq 0); Out = $out }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$script:testCount = 0

# ---------------------------------------------------------------------------
Stage "build is clean"

# TreatWarningsAsErrors is on, so a warning is already a failure. This stage exists
# for the case where somebody turns it off: the harness should still refuse.
$out = & dotnet build AuthCoexistence.slnx -c Release --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) {
    $out | Where-Object { $_ -match 'error' } | ForEach-Object { Write-Host "  $_" }
    Fail "build failed"
}
$warnings = $out | Select-String -Pattern ': warning '
if ($warnings) {
    $warnings | ForEach-Object { Write-Host "  $($_.Line)" -ForegroundColor Yellow }
    Fail "$($warnings.Count) compiler warning(s)"
}
Write-Host "  clean, no warnings"

# ---------------------------------------------------------------------------
Stage "cryptographic primitives against published vectors"

# Run first and separately. Everything downstream -- the password migration, the
# ticket forgery results, the token hardening table -- is only meaningful if Blake2b,
# Argon2 and PBKDF2 agree with RFC 7693, RFC 9106 and RFC 6070. If this stage is red,
# nothing else in the report is evidence about anything.
$r = RunTests 'FullyQualifiedName~Blake2bTests|FullyQualifiedName~Argon2Tests'
if (-not $r.Ok) {
    $r.Out | Select-String -Pattern '\[FAIL\]' | ForEach-Object { Write-Host "  $($_.Line)" }
    Fail "primitives do not match the published vectors"
}
$summary = $r.Out | Select-String -Pattern 'Total:\s*(\d+)' | Select-Object -Last 1
if ("$summary" -match 'Total:\s*(\d+)') { Write-Host "  $($Matches[1]) vector and property checks" }

# ---------------------------------------------------------------------------
Stage "full suite"

$r = RunTests $null
if (-not $r.Ok) {
    $r.Out | Select-String -Pattern '\[FAIL\]|Error Message' | ForEach-Object { Write-Host "  $($_.Line)" }
    Fail "test suite is red"
}
$summary = $r.Out | Select-String -Pattern 'Total:\s*(\d+)' | Select-Object -Last 1
if ("$summary" -match 'Total:\s*(\d+)') { $script:testCount = [int]$Matches[1] }
if ($script:testCount -lt 250) { Fail "only $script:testCount tests ran; expected at least 250" }
Write-Host "  $script:testCount tests"

# ---------------------------------------------------------------------------
Stage "the report still says what it says"

# Two files, on purpose -- see docs/adr/005-two-report-files.md. results.md carries
# timings, which cannot be stable across machines and must not be compared.
# results-stable.md carries only the claims: divergence counts, counterexamples,
# migration curves, prediction verdicts. Those are either reproducible or they were
# never results.
$stable = Join-Path $PSScriptRoot 'docs\results-stable.md'
if (-not (Test-Path $stable)) { Fail "docs/results-stable.md is missing" }
$before = [IO.File]::ReadAllBytes($stable)

# `dotnet run --project X -- report docs` drops the arguments after `--` on this SDK,
# so the built executable is invoked directly.
& (Join-Path $PSScriptRoot 'Auth.Report\bin\Release\net10.0\Auth.Report.exe') report docs 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "report generator exited non-zero" }

$after = [IO.File]::ReadAllBytes($stable)
if ($before.Length -ne $after.Length -or (Compare-Object $before $after -SyncWindow 0)) {
    Fail "docs/results-stable.md changed when regenerated; read the diff, then commit it"
}
Write-Host "  $($after.Length) bytes, byte-identical after regeneration"

$text = [Text.Encoding]::UTF8.GetString($after)

# The three headline numbers, read out of the committed file rather than trusted from
# the suite. 592 divergences under the naive table, 16 once deny rows exist, 0 once
# the one wrong grant row is corrected.
foreach ($claim in '| 592 | 592 | 0 |', '**16**', '72 counterexamples') {
    if (-not $text.Contains($claim)) { Fail "the report no longer contains: $claim" }
}
Write-Host "  headline claims present (592 -> 16 -> 0, 72 counterexamples)"

# The control that makes the escalation finding mean anything. If some divergences
# had been lockouts, the "it passes UAT because nobody reports it" argument collapses.
if ($text -notmatch '(\d+) escalations, 0 lockouts') {
    Fail "the report does not state that every naive divergence was an escalation"
}
Write-Host "  every divergence is an escalation ($($Matches[1]) of them, 0 lockouts)"

# A scoreboard that scores only the comfortable predictions is not a scoreboard. The
# backreference means no threshold has to be maintained by hand.
if ($text -notmatch '(\d+) predictions were written before the experiments were run') {
    Fail "the prediction scoreboard header is missing"
}
$declared = [int]$Matches[1]
$rows = ([regex]::Matches($text, '\| (?:held|contradicted|timing) \|')).Count
if ($rows -ne $declared) { Fail "header claims $declared predictions but the table has $rows rows" }
Write-Host "  $declared predictions declared, $rows scored"

# ---------------------------------------------------------------------------
Stage "mutation: would the suite notice if the guarantees were removed?"

# Six edits. Each one deletes a single check that one of the report's findings
# depends on. A survivor means the corresponding sentence in results.md is an
# unverified assertion.
#
# The mutations are chosen to hit *claims*, not lines. Two of them attack the
# non-monotone legacy policy, because "a monotone function cannot reproduce it" is
# the intellectual core; one attacks the deny channel that reduces 592 to 16; one
# attacks the uniform rejection reason that closes the padding-oracle; one attacks
# the missing-exp rejection; one attacks the Argon2 variable-length hash, which
# would silently change every stored password hash in the fleet.
$mutations = @(
    @{ File  = 'Auth.Bridge\LegacyAuthorization.cs'
       From  = '(In(LegacyRoles.Admin) || In(LegacyRoles.Clerk)) && !In(LegacyRoles.Auditor),'
       To    = '(In(LegacyRoles.Admin) || In(LegacyRoles.Clerk)),'
       Kills = 'the segregation-of-duties clause on the ledger -- the source of most of the 72 counterexamples' }

    @{ File  = 'Auth.Bridge\LegacyAuthorization.cs'
       From  = 'In(LegacyRoles.Admin) && !In(LegacyRoles.Vendor) && !In(LegacyRoles.Temp),'
       To    = 'In(LegacyRoles.Admin) && !In(LegacyRoles.Vendor),'
       Kills = 'the Temp exclusion on user management, so adding Temp stops removing an ability' }

    @{ File  = 'Auth.Bridge\ClaimsTransformation.cs'
       From  = '[LegacyRoles.Auditor] = [Resources.WriteLedger],'
       To    = '[LegacyRoles.Auditor] = [],'
       Kills = 'the deny row that takes the ledger away from an auditor -- the 592-to-16 reduction' }

    @{ File  = 'Auth.Bridge\LegacyAuthorization.cs'
       From  = 'public bool Has(string scope) => !DenyScopes.Contains(scope) && Scopes.Contains(scope);'
       To    = 'public bool Has(string scope) => Scopes.Contains(scope);'
       Kills = 'deny-wins resolution, so the negative channel is carried but never consulted' }

    @{ File  = 'Auth.Modern\JwtCodec.cs'
       From  = @'
        if (claims["exp"] is not { } exp)
        {
            return new JwtValidation(null, JwtFailure.MissingExpiry);
        }
'@
       To    = @'
        if (claims["exp"] is not { } exp)
        {
            return new JwtValidation(claims, JwtFailure.None);
        }
'@
       Kills = 'the missing-exp rejection: a token with no expiry becomes a token that never expires' }

    @{ File  = 'Auth.Passwords\Argon2.cs'
       From  = 'if (i < r - 1) v = Blake2b.Hash(v, 64);'
       To    = 'v = Blake2b.Hash(v, 64);'
       Kills = 'the off-by-one in the variable-length hash: every Argon2 tag in the fleet changes' }
)

$originals = @{}
foreach ($m in $mutations) {
    $path = Join-Path $PSScriptRoot $m.File
    if (-not (Test-Path $path)) { Fail "mutation target file not found: $($m.File)" }
    if (-not $originals.ContainsKey($m.File)) {
        $originals[$m.File] = [IO.File]::ReadAllText($path)
    }
    if (-not $originals[$m.File].Contains($m.From)) {
        Fail "mutation target not found in $($m.File): $($m.From)"
    }
}

$killed = 0
foreach ($m in $mutations) {
    $path = Join-Path $PSScriptRoot $m.File
    [IO.File]::WriteAllText($path, $originals[$m.File].Replace($m.From, $m.To))
    try {
        # A mutation that does not compile is a broken harness, not a killed mutant.
        $b = & dotnet build AuthCoexistence.slnx -c Release --nologo -v q --no-restore 2>&1
        if ($LASTEXITCODE -ne 0) {
            $b | Where-Object { $_ -match 'error' } | Select-Object -First 3 |
                ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
            Fail "mutated source did not build: $($m.Kills)"
        }

        # The report tests take minutes and none of these mutations is aimed at
        # report formatting, so the mutation runs use the rest of the suite. If a
        # mutation ever needs ReportTests to be caught, that is a signal the
        # behavioural tests are too weak, not that the filter is wrong.
        $r = RunTests 'FullyQualifiedName!~ReportTests'
        if ($r.Ok) {
            Write-Host "  SURVIVED  $($m.Kills)" -ForegroundColor Red
        }
        else {
            $killed++
            Write-Host "  killed    $($m.Kills)"
        }
    }
    finally {
        [IO.File]::WriteAllText($path, $originals[$m.File])
    }
}

& dotnet build AuthCoexistence.slnx -c Release --nologo -v q --no-restore 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "the tree did not rebuild after mutations were reverted" }

if ($killed -ne $mutations.Count) {
    Fail "$($mutations.Count - $killed) of $($mutations.Count) mutations survived"
}
Write-Host "  $killed/$($mutations.Count) killed"

# ---------------------------------------------------------------------------
Stage "secrets"

# This project generates keys, tickets, tokens and password hashes. Every one of them
# should be produced at runtime; none should be committed.
#
# The credential-literal pattern is applied to production code only, and the reason is
# not that tests deserve less scrutiny -- it is that a test *must* contain literal
# passwords to test password handling, so the pattern has a 100% false-positive rate
# there and would be silenced within a week. Instead the production assemblies get a
# stricter rule than the pattern could express: they must contain no key or password
# literal at all, because every key in them comes from RandomNumberGenerator.
$universal = @(
    'AKIA[0-9A-Z]{16}',
    'ghp_[A-Za-z0-9]{36}',
    'sk-[A-Za-z0-9]{32,}',
    '-----BEGIN [A-Z ]*PRIVATE KEY-----'
)
$productionOnly = @(
    'password\s*=\s*["''][^"'']{8,}["'']',
    '(?i)(validation|encryption|signing)key\s*=\s*["''][^"'']+["'']',
    '(?i)connectionstring\s*=\s*["''][^"'']*(pwd|password)='
)

$all = Get-ChildItem -Recurse -File -Include *.cs, *.md, *.ps1, *.csproj, *.slnx, *.json |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' }
$production = $all | Where-Object { $_.FullName -notmatch '\\Auth\.Tests\\' }

$found = @()
foreach ($p in $universal)      { $found += $all        | Select-String -Pattern $p -AllMatches }
foreach ($p in $productionOnly) { $found += $production | Select-String -Pattern $p -AllMatches }

if ($found.Count -gt 0) {
    $found | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)  $($_.Line.Trim())" -ForegroundColor Red }
    Fail "$($found.Count) possible secret(s)"
}

# The stricter rule, stated as a check rather than a comment: no production source file
# may hold a byte-array literal that looks like a key. `new byte[] { 0x.., ... }` with
# 16 or more entries is a key, an IV or a test vector, and test vectors do not belong in
# the authentication path.
$keyish = $production | Select-String -Pattern 'new byte\[\]\s*\{\s*(0x[0-9A-Fa-f]{2},\s*){16,}'
if ($keyish) {
    $keyish | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)" -ForegroundColor Red }
    Fail "key-shaped byte literal in production code"
}

Write-Host "  $($all.Count) files scanned ($($production.Count) production), nothing found"

# ---------------------------------------------------------------------------
$sw.Stop()
Write-Host ""
Write-Host ("=" * 78)
Write-Host ("  all 6 stages passed -- {0} tests, {1}/{2} mutations killed, {3:N1}s" -f `
    $script:testCount, $killed, $mutations.Count, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host ("=" * 78)
exit 0
