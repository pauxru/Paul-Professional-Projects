# Runs the walkthrough: one account, from a Forms cookie in 2010 to an OIDC token,
# printed step by step.
#
# The report answers population questions -- how many decisions diverge, how long the
# tail is, how much the padding costs. This answers the individual one: what actually
# happens to a single user, in order, and which door is open at each point. The
# findings are easier to believe once you have watched the mechanics that produce them.
#
# -Report regenerates docs/results.md and docs/results-stable.md instead. That takes a
# few minutes, most of it in the Argon2 timing harness.
param([switch]$Report)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$exe = Join-Path $root 'Auth.Report\bin\Release\net10.0\Auth.Report.exe'

if (-not (Test-Path $exe)) {
    Write-Host '==> building first' -ForegroundColor Cyan
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

# Invoked as an executable rather than `dotnet run --project X -- demo`, which drops
# everything after the `--` on this SDK and silently runs the default mode.
if ($Report) {
    Write-Host '==> regenerating docs/results.md and docs/results-stable.md' -ForegroundColor Cyan
    Write-Host '    (several minutes; the timing harness runs Argon2id at the deployable profile)'
    & $exe report (Join-Path $root 'docs')
}
else {
    & $exe demo
}

if ($LASTEXITCODE -ne 0) { throw "Auth.Report exited with $LASTEXITCODE" }
