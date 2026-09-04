# Builds the three DLL variants, the native self-test, and the managed solution.
#
# The three DLLs are compiled from identical sources and differ only in preprocessor
# definitions, because that is the claim the whole project rests on: the danger lives in
# the boundary, not in the mathematics. If the build ever compiled them from different
# sources the experiment would be measuring nothing.
#
# -NativeOnly skips the managed half. The mutation stage of test.ps1 rebuilds the C++
# once per mutant and would otherwise spend most of its time relinking managed
# assemblies that no mutation touches.
param([switch]$NativeOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot

$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) {
    # The project needs a C++20 compiler, not one specific edition of Visual Studio.
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -property installationPath
        if ($install) { $vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat' }
    }
}
if (-not (Test-Path $vcvars)) { throw "no MSVC toolchain found (looked for $vcvars)" }

Write-Host '==> building the native DLLs (hardened, legacy, fast)' -ForegroundColor Cyan
# cmake, ninja and cl all come from the developer environment, so configure and build
# have to happen inside the same cmd process that sourced vcvars. Running vcvars in one
# call and cl in another leaves PATH, LIB and INCLUDE unset where it matters.
#
# The build tree is native/build and not build/, because Bridge.Core's DLL resolver
# walks up from the assembly location looking for exactly native/build/bin/pricing.dll.
# It refuses to fall back to "some pricing.dll somewhere nearby" -- a resolver that
# loads whichever DLL it happens to find is a supply-chain problem wearing a
# convenience costume -- so the build has to put the output where the contract says.
$build = Join-Path $root 'native\build'
$cmd = "call `"$vcvars`" >nul && cd /d `"$root\native`" && " +
       "cmake -S . -B `"$build`" -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo && " +
       "cmake --build `"$build`""
& $env:ComSpec /c $cmd
if ($LASTEXITCODE -ne 0) { throw "native build failed with exit code $LASTEXITCODE" }

foreach ($dll in 'pricing.dll', 'pricing_legacy.dll', 'pricing_fast.dll', 'native_selftest.exe') {
    $path = Join-Path $build "bin\$dll"
    if (-not (Test-Path $path)) { throw "expected $path to exist after the build" }
}

Write-Host '==> building the managed solution' -ForegroundColor Cyan
if ($NativeOnly) {
    Write-Host '    skipped (-NativeOnly)'
    Write-Host '==> build complete' -ForegroundColor Green
    return
}
Push-Location $root
try {
    # TreatWarningsAsErrors is set in the projects; this is just the invocation.
    & dotnet build CrownJewelsBridge.slnx -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "managed build failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }

Write-Host '==> build complete' -ForegroundColor Green
