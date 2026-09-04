# Builds the solution and, unless told otherwise, regenerates nothing.
#
# There is no native half here and no code generation: the interesting part of this
# project is that five assemblies with incompatible ideas about identity compile
# against one another and produce a single principal type. So the build is short, and
# what matters is that it is warning-clean -- TreatWarningsAsErrors is set in every
# csproj, because a nullability warning in authentication code is a security finding
# with a friendly name.
#
# -SkipRestore is for the mutation stage of test.ps1, which rebuilds once per mutant
# and would otherwise spend most of its wall clock re-resolving packages that have
# not moved.
param([switch]$SkipRestore)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
Push-Location $root
try {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw 'dotnet SDK not found on PATH' }

    $sdk = & dotnet --version
    Write-Host "==> .NET SDK $sdk" -ForegroundColor Cyan

    # dotnet build takes exactly one project or solution. Passing the .slnx keeps the
    # five assemblies in one graph so a break in Auth.Passwords surfaces immediately
    # rather than three commands later.
    $args = @('build', 'AuthCoexistence.slnx', '-c', 'Release', '--nologo', '-v', 'q')
    if ($SkipRestore) { $args += '--no-restore' }

    Write-Host '==> building AuthCoexistence.slnx' -ForegroundColor Cyan
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

    # The report generator is invoked as an executable rather than through
    # `dotnet run --project X -- args`, which swallows everything after the `--` on
    # this SDK. Checking for it here turns a confusing "unknown mode" later into a
    # clear failure now.
    $exe = Join-Path $root 'Auth.Report\bin\Release\net10.0\Auth.Report.exe'
    if (-not (Test-Path $exe)) { throw "expected $exe to exist after the build" }
}
finally { Pop-Location }

Write-Host '==> build complete' -ForegroundColor Green
