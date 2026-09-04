param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CargoArgs)

if (-not $CargoArgs -or $CargoArgs.Count -eq 0) { $CargoArgs = @("build", "--offline") }
$joined = $CargoArgs -join " "

$vc = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
$h = $env:USERPROFILE
$cmd = "call `"$vc`" >nul 2>&1 && set CARGO_HOME=$h\toolchains\cargo&& set RUSTUP_HOME=$h\toolchains\rustup&& set PATH=$h\toolchains\cargo\bin;%PATH%&& cargo $joined"
& $env:ComSpec /c $cmd
exit $LASTEXITCODE
