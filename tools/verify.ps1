# Runs every project's own test suite and records what actually happened.
#
# The README makes numeric claims -- test counts, pass rates -- and a claim in a README
# is worth exactly as much as the command that regenerates it. This is that command.
#
# Each project is trusted to know how to test itself: if it ships a test.ps1 that script
# is authoritative, because several of them do more than run unit tests (report
# byte-comparison, determinism re-runs, source mutation). Only where no test.ps1 exists
# does this fall back to `dotnet test` against the solution.
#
# Results are appended line-by-line and flushed, so a sweep that dies halfway still tells
# you where it got to.

[CmdletBinding()]
param(
    [string] $Filter = '',
    [string] $Out = (Join-Path $PSScriptRoot 'verification.tsv')
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$projectsRoot = Join-Path $repo 'projects'

$shell = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $shell) {
    Write-Host "PowerShell 7 (pwsh) is required: several test.ps1 scripts use APIs that Windows PowerShell 5.1 does not have." -ForegroundColor Red
    exit 2
}

# Test frameworks each report their totals differently. Rather than guess which one a
# project uses, apply every pattern; a project only ever matches its own.
$patterns = @(
    @{ Name = 'vstest'; Re = 'Passed!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)'; Fail = 1; Pass = 2; Skip = 3 },
    @{ Name = 'vstest-failed'; Re = 'Failed!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)'; Fail = 1; Pass = 2; Skip = 3 },
    @{ Name = 'surefire'; Re = 'Tests run:\s+(\d+),\s+Failures:\s+(\d+),\s+Errors:\s+(\d+),\s+Skipped:\s+(\d+)'; Total = 1; Fail = 2; Err = 3; Skip = 4 },
    @{ Name = 'cargo'; Re = 'test result:\s+\w+\.\s+(\d+)\s+passed;\s+(\d+)\s+failed'; Pass = 1; Fail = 2 },
    @{ Name = 'pytest-fail'; Re = '(\d+)\s+failed,\s+(\d+)\s+passed'; Fail = 1; Pass = 2 },
    @{ Name = 'pytest'; Re = '^(?:=+\s*)?(\d+)\s+passed(?:,\s+(\d+)\s+skipped)?'; Pass = 1; Skip = 2 },
    @{ Name = 'unittest'; Re = 'Ran\s+(\d+)\s+tests?\s+in\s+[\d.]+s'; Total = 1 },
    @{ Name = 'unittest-fail'; Re = 'FAILED\s+\((?:failures=(\d+))?(?:,\s*)?(?:errors=(\d+))?'; Fail = 1; Err = 2 },
    @{ Name = 'gotest'; Tally = $true },
    @{ Name = 'ctest'; Re = '(\d+)% tests passed,\s+(\d+) tests failed out of\s+(\d+)'; Fail = 2; Total = 3 },
    @{ Name = 'selftest'; Re = '(\d+)\s*/\s*(\d+)\s+checks passed'; Pass = 1; Total = 2 }
)

function Measure-Output {
    param([string[]] $Lines)
    $text = ($Lines -join "`n")
    $pass = 0; $fail = 0; $skip = 0; $how = ''

    # The 31-50 harnesses end with their own summary line, and it is the most
    # authoritative number available: the project counted its own tests, across however
    # many suites and languages it runs. Take it and stop -- letting the framework
    # patterns below also match would count the same tests twice.
    $summary = [regex]::Match($text, 'all \d+ stages passed\s*--\s*(\d+)\s+tests')
    if ($summary.Success) {
        return @{ Passed = [int]$summary.Groups[1].Value; Failed = 0; Skipped = 0; How = 'harness' }
    }

    foreach ($p in $patterns) {
        if ($p.Tally) {
            $ok = ([regex]::Matches($text, '^\s*--- PASS', 'Multiline')).Count
            $no = ([regex]::Matches($text, '^\s*--- FAIL', 'Multiline')).Count
            if ($ok + $no -gt 0) { $pass += $ok; $fail += $no; $how = 'gotest' }
            continue
        }
        foreach ($m in [regex]::Matches($text, $p.Re, 'Multiline')) {
            $how = $p.Name
            if ($p.Total) {
                $t = [int]$m.Groups[$p.Total].Value
                $f = if ($p.Fail) { [int]$m.Groups[$p.Fail].Value } else { 0 }
                if ($p.Err) { $f += [int]$m.Groups[$p.Err].Value }
                $s = if ($p.Skip) { [int]$m.Groups[$p.Skip].Value } else { 0 }
                $pass += ($t - $f - $s); $fail += $f; $skip += $s
            } else {
                if ($p.Pass) { $pass += [int]$m.Groups[$p.Pass].Value }
                if ($p.Fail) { $fail += [int]$m.Groups[$p.Fail].Value }
                if ($p.Skip -and $m.Groups[$p.Skip].Success) { $skip += [int]$m.Groups[$p.Skip].Value }
            }
        }
    }
    return @{ Passed = $pass; Failed = $fail; Skipped = $skip; How = $how }
}

$projects = Get-ChildItem $projectsRoot -Directory |
    ForEach-Object { Get-ChildItem $_.FullName -Directory } |
    Where-Object { $_.Name -match '^\d\d-' } |
    Sort-Object Name

if ($Filter) { $projects = $projects | Where-Object { $_.Name -match $Filter } }

"project`tstatus`tpassed`tfailed`tskipped`tseconds`trunner`tparser" | Set-Content $Out -Encoding utf8
$logDir = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$grand = @{ Passed = 0; Failed = 0; Projects = 0; Green = 0 }

foreach ($p in $projects) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ("-> {0}" -f $p.Name) -ForegroundColor Cyan

    $runner = 'none'
    $lines = @()
    $code = -1
    Push-Location $p.FullName
    try {
        if (Test-Path (Join-Path $p.FullName 'test.ps1')) {
            $runner = 'test.ps1'
            # Compiled projects keep build and test separate on purpose: test.ps1 refuses
            # to run against a stale or missing binary rather than silently testing
            # whatever was lying around from last time. That is the right behaviour for
            # the project and it means the sweep has to do the build itself.
            if (Test-Path (Join-Path $p.FullName 'build.ps1')) {
                $lines += "=== verify.ps1: build.ps1 ==="
                $lines += & $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $p.FullName 'build.ps1') 2>&1 |
                          ForEach-Object { "$_" }
                if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed with exit code $LASTEXITCODE" }
                $lines += "=== verify.ps1: test.ps1 ==="
            }
            # pwsh, not powershell. The scripts target PowerShell 7 -- several use
            # SHA256::HashData, which does not exist in Windows PowerShell 5.1, and
            # the failure mode there is a MethodNotFound five stages into a sweep.
            $lines += & $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $p.FullName 'test.ps1') 2>&1 | ForEach-Object { "$_" }
            $code = $LASTEXITCODE
        } elseif (@(Get-ChildItem $p.FullName -Filter '*.sln*' -File).Count -gt 0) {
            $runner = 'dotnet test'
            $lines = & dotnet test -c Release --nologo -v q 2>&1 | ForEach-Object { "$_" }
            $code = $LASTEXITCODE
        }
    } catch {
        $lines += "EXCEPTION: $_"
        $code = -2
    } finally {
        Pop-Location
    }
    $sw.Stop()

    $lines | Set-Content (Join-Path $logDir "$($p.Name).log") -Encoding utf8

    $m = Measure-Output -Lines $lines

    # `go test` without -v prints one line per package, not per test. A Go project that
    # passes therefore reports either zero tests, or -- worse -- a small number picked up
    # from whichever few packages the project's own script happened to run verbosely.
    # A plausible-looking undercount is more dangerous than no count at all, because
    # nothing about it looks wrong on the way to the README. So treat "fewer tests than
    # packages that reported ok" as proof the tally is not a test count, and re-run the
    # suite verbosely purely to count. It is the same tests; a disagreement between the
    # two runs would itself be a finding worth chasing.
    $okPkgs = ([regex]::Matches(($lines -join "`n"), '^ok\s', 'Multiline')).Count
    $goMod = Get-ChildItem -Path $p.FullName -Filter 'go.mod' -Recurse -Depth 3 -File -EA SilentlyContinue |
             Select-Object -First 1
    if ($code -eq 0 -and $goMod -and $m.How -ne 'harness' -and ($m.Passed -eq 0 -or $m.Passed -lt $okPkgs)) {
        Push-Location $goMod.Directory.FullName
        $v = & go test -count=1 -v ./... 2>&1 | ForEach-Object { "$_" }
        Pop-Location
        $gm = Measure-Output -Lines $v
        if ($gm.Passed -gt $m.Passed) {
            $m = $gm
            $m.How = 'gotest -v'
        }
    }
    $status = if ($runner -eq 'none') { 'NO_RUNNER' }
              elseif ($code -ne 0) { 'FAIL' }
              elseif ($m.Failed -gt 0) { 'FAIL' }
              elseif ($m.Passed -eq 0) { 'NO_TESTS_DETECTED' }
              else { 'PASS' }

    $grand.Projects++
    $grand.Passed += $m.Passed
    $grand.Failed += $m.Failed
    if ($status -eq 'PASS') { $grand.Green++ }

    Add-Content -Path $Out -Encoding utf8 -Value ("{0}`t{1}`t{2}`t{3}`t{4}`t{5:N1}`t{6}`t{7}" -f `
        $p.Name, $status, $m.Passed, $m.Failed, $m.Skipped, $sw.Elapsed.TotalSeconds, $runner, $m.How)

    Write-Host ("   {0}  passed={1} failed={2}  {3:N0}s" -f $status, $m.Passed, $m.Failed, $sw.Elapsed.TotalSeconds) `
        -ForegroundColor $(if ($status -eq 'PASS') { 'Green' } else { 'Red' })
}

Write-Host ""
Write-Host ("SWEEP COMPLETE: {0}/{1} projects green, {2} tests passed, {3} failed" -f `
    $grand.Green, $grand.Projects, $grand.Passed, $grand.Failed) -ForegroundColor Yellow
Add-Content -Path $Out -Encoding utf8 -Value ("TOTAL`t{0}/{1}`t{2}`t{3}`t`t`t`t" -f `
    $grand.Green, $grand.Projects, $grand.Passed, $grand.Failed)
exit ([int]($grand.Failed -gt 0))
