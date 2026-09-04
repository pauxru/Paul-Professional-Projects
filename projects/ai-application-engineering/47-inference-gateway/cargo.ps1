# Wrapper: MSVC environment + an offline, project-local CARGO_HOME.
#
# Callers must QUOTE the argument separator: .\cargo.ps1 run --release '--' --stdout
# An unquoted `--` is consumed by PowerShell's own parameter binder and never
# reaches this script, so cargo sees the trailing arguments as its own and
# rejects them. This cost an hour; see docs/known-limitations.md.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CargoArgs)

if (-not $CargoArgs -or $CargoArgs.Count -eq 0) { $CargoArgs = @("build", "--offline") }
$joined = $CargoArgs -join " "

$vc = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
$h = $env:USERPROFILE
$cmd = "call `"$vc`" >nul 2>&1 && set CARGO_HOME=$h\toolchains\cargo&& set RUSTUP_HOME=$h\toolchains\rustup&& set PATH=$h\toolchains\cargo\bin;%PATH%&& cargo $joined"
& $env:ComSpec /c $cmd
exit $LASTEXITCODE
