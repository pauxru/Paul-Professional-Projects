# Seven stages.
#
# Stage 4 is the one that justifies the project. Everything else here checks that
# the code does what it says; stage 4 checks that the *report* does, by regenerating
# it and demanding the claims come out byte-identical. A project whose entire output
# is a set of measurements cannot ship measurements nobody re-ran.
#
# Assumes build.ps1 has already produced native/build/bin. Exits non-zero on the
# first failure.

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
    $out = & dotnet test Bridge.Tests\Bridge.Tests.csproj -c Release -v q --nologo 2>&1
    return @{ Ok = ($LASTEXITCODE -eq 0); Out = $out }
}

$sw = [Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------
Stage "the three DLLs exist and are actually different"

# The entire claim of this project is "same C++, two boundaries". If the build
# emitted one DLL and copied it three times -- which is exactly what a broken
# CMake variant target does, silently -- every comparison downstream would be a
# tautology reporting perfect agreement.
$bin = Join-Path $PSScriptRoot 'native\build\bin'
if (-not (Test-Path $bin)) { Fail "$bin does not exist; run build.ps1 first" }

$dlls = @('pricing.dll', 'pricing_legacy.dll', 'pricing_fast.dll')
$hashes = @{}
foreach ($d in $dlls) {
    $path = Join-Path $bin $d
    if (-not (Test-Path $path)) { Fail "missing $d" }
    $h = (Get-FileHash $path -Algorithm SHA256).Hash.Substring(0, 16).ToLower()
    $hashes[$d] = $h
    Write-Host ("  {0,-20} {1}  {2:N0} bytes" -f $d, $h, (Get-Item $path).Length)
}
if (($hashes.Values | Select-Object -Unique).Count -ne 3) {
    Fail "two of the three DLLs are byte-identical; the variant build is not varying anything"
}
Write-Host "  three distinct binaries"

# ---------------------------------------------------------------------------
Stage "native self-test"

# Runs the C++ engine with no CLR in the process at all. If this fails, the bug
# is in the mathematics and nothing about the marshalling layer is worth reading.
$selftest = Join-Path $bin 'native_selftest.exe'
if (-not (Test-Path $selftest)) { Fail "missing native_selftest.exe" }
$out = & $selftest 2>&1
if ($LASTEXITCODE -ne 0) {
    $out | ForEach-Object { Write-Host "  $_" }
    Fail "native self-test failed"
}
$native = 0
if ("$out" -match '(\d+)\s+checks') { $native = [int]$Matches[1] }
$out | Select-Object -Last 1 | ForEach-Object { Write-Host "  $_" }

# ---------------------------------------------------------------------------
Stage "managed build"

$out = & dotnet build CrownJewelsBridge.slnx -c Release --nologo -v q 2>&1
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

$out = & dotnet test Bridge.Tests\Bridge.Tests.csproj -c Release --nologo 2>&1
$summary = $out | Select-String -Pattern 'Passed!.*Total:\s*\d+' | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) {
    $out | Select-String -Pattern '\[FAIL\]|Error Message' | ForEach-Object { Write-Host "  $($_.Line)" }
    Fail "test suite is red"
}
if ("$summary" -match 'Total:\s*(\d+)') { $script:testCount = [int]$Matches[1] }
if ($script:testCount -lt 150) { Fail "only $script:testCount tests ran; expected at least 150" }
Write-Host "  $script:testCount managed tests, $native native checks"

# ---------------------------------------------------------------------------
Stage "the report still says what it says"

# Two files, on purpose. results.md carries timings, which cannot be stable
# across machines and must not be compared. results-stable.md carries only the
# claims -- outcome counts, prediction verdicts, prices -- and those are either
# reproducible or they were never results.
#
# This takes a few minutes. It re-runs six hundred fuzz cases through three DLLs
# in isolated child processes, which is the cost of the finding being real.
$stable = Join-Path $PSScriptRoot 'docs\results-stable.md'
if (-not (Test-Path $stable)) { Fail "docs/results-stable.md is missing" }
$before = [IO.File]::ReadAllBytes($stable)

& (Join-Path $PSScriptRoot 'Bridge.Report\bin\Release\net10.0\Bridge.Report.exe') report docs 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "report generator exited non-zero" }

$after = [IO.File]::ReadAllBytes($stable)
if ($before.Length -ne $after.Length -or (Compare-Object $before $after -SyncWindow 0)) {
    Fail "docs/results-stable.md changed when regenerated; commit the new version"
}
Write-Host "  $($after.Length) bytes, byte-identical after regeneration"

# The control group is the reason any of the numbers mean anything. A run where
# the hardened boundary refused legal input scores a perfect sheet on the hostile
# cases and is worthless. Read it out of the file rather than trusting the suite.
# The backreference is the whole check: "accepted 150 of 150" passes, "accepted
# 149 of 150" does not, and no threshold has to be maintained by hand.
$text = [Text.Encoding]::UTF8.GetString($after)
if ($text -notmatch 'accepted (\d+) of \1') {
    Fail "the report does not state that the control group was accepted in full"
}
Write-Host "  control group held ($($Matches[1]) of $($Matches[1]) legal inputs accepted)"

# ---------------------------------------------------------------------------
Stage "mutation: would the suite notice if the boundary stopped guarding?"

# Six single-token edits in the hardened boundary, each removing one check the
# report's headline depends on. A survivor means "449 unsafe outcomes became 0"
# is an unverified sentence.
#
# Every mutation is in abi.cpp, never in engine.cpp -- mutating the shared engine
# would prove only that the tests notice broken arithmetic, which is not the
# claim under test. Each one requires a native rebuild, so this stage is slow by
# construction.
$abi = Join-Path $PSScriptRoot 'native\src\abi.cpp'
$mutations = @(
    @{ From  = 'if (steps < 1) {'
       To    = 'if (steps < 0) {'
       Kills = 'the zero-step lattice, which returns 0.0 for an option worth 0.91 and reports success' }

    @{ From  = 'if (out_capacity < count) {'
       To    = 'if (out_capacity < 0) {'
       Kills = 'the batch capacity check, which is the 63-slot heap overwrite' }

    @{ From  = 'if (o.volatility < 0.0)           return "volatility must not be negative";'
       To    = 'if (o.volatility < -1e308)        return "volatility must not be negative";'
       Kills = 'the negative-volatility theorem: a sign error prices the opposite instrument, bookably' }

    @{ From  = 'if (o.years < 0.0)                return "years must not be negative";'
       To    = 'if (o.years < -1e308)             return "years must not be negative";'
       Kills = 'negative time to expiry, which prices an option that already knows its own outcome' }

    @{ From  = 'if (o.strike > o.spot * kMaxMoneyness)'
       To    = 'if (o.strike > o.spot * 1e300)'
       Kills = 'the moneyness check, so strike 1e300 returns exactly 0.0 and looks plausible' }

    @{ From  = 'if (!std::isfinite(o.spot))       return "spot is not a finite number";'
       To    = 'if (!std::isfinite(o.strike))     return "spot is not a finite number";'
       Kills = 'the finiteness check on spot, so a NaN feed reaches the arithmetic' }
)

$originalAbi = [IO.File]::ReadAllText($abi)
$killed = 0

foreach ($m in $mutations) {
    if (-not $originalAbi.Contains($m.From)) { Fail "mutation target not found in abi.cpp: $($m.From)" }

    [IO.File]::WriteAllText($abi, $originalAbi.Replace($m.From, $m.To))
    try {
        & (Join-Path $PSScriptRoot 'build.ps1') -NativeOnly 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            # A mutation that will not compile is not evidence about the suite.
            Fail "mutated abi.cpp did not build: $($m.Kills)"
        }

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
        [IO.File]::WriteAllText($abi, $originalAbi)
    }
}

& (Join-Path $PSScriptRoot 'build.ps1') -NativeOnly 2>&1 | Out-Null

if ($killed -ne $mutations.Count) {
    Fail "$($mutations.Count - $killed) of $($mutations.Count) mutations survived"
}
Write-Host "  $killed/$($mutations.Count) killed"

# ---------------------------------------------------------------------------
Stage "secrets"

$patterns = @(
    'AKIA[0-9A-Z]{16}',
    'ghp_[A-Za-z0-9]{36}',
    'sk-[A-Za-z0-9]{32,}',
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    'password\s*=\s*["''][^"'']{4,}["'']'
)

$files = Get-ChildItem -Recurse -File -Include *.cs, *.cpp, *.h, *.md, *.ps1, *.csproj, *.slnx, *.txt |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|build|\.git)\\' }

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
Write-Host ("  all 7 stages passed -- {0} tests, {1}/{2} mutations killed, {3:N1}s" -f `
    ($script:testCount + $native), $killed, $mutations.Count, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host ("=" * 78)
exit 0
