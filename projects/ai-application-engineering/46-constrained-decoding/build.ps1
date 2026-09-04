# Builds the native core and prepares the Python environment.
# Fails loudly: a build script that reports success after a failed compile is
# worse than no build script.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) {
    # Fall back to whatever the machine has; the project only needs a C++20
    # compiler, not this specific edition.
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -property installationPath
        if ($install) { $vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat' }
    }
}
if (-not (Test-Path $vcvars)) { throw "no MSVC toolchain found (looked for $vcvars)" }

Write-Host '==> configuring and building the native core'
# cmake, ninja and cl all come from the developer environment, so the configure
# and build must happen inside the same cmd process that sourced vcvars.
$cmd = "call `"$vcvars`" >nul && cd /d `"$root`" && cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo && cmake --build build"
& $env:ComSpec /c $cmd
if ($LASTEXITCODE -ne 0) { throw "native build failed with exit code $LASTEXITCODE" }

$dll = Join-Path $root 'build\lib\cdec.dll'
if (-not (Test-Path $dll)) { throw "expected $dll to exist after the build" }

Write-Host '==> preparing the Python environment'
$python = 'C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe'
if (-not (Test-Path $python)) { $python = (Get-Command python3 -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'no usable Python interpreter found' }

$venvPython = Join-Path $root '.venv\Scripts\python.exe'
if (-not (Test-Path $venvPython)) {
    & $python -m venv (Join-Path $root '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'venv creation failed' }
}
& $venvPython -m pip install --quiet --disable-pip-version-check pytest
if ($LASTEXITCODE -ne 0) { throw 'pip install failed' }

Write-Host '==> build complete'
