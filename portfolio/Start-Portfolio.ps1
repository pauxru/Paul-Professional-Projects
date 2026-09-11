param(
    [ValidateSet('dev', 'preview')]
    [string]$Mode = 'dev'
)

$ErrorActionPreference = 'Stop'
$originalPath = $env:Path
Push-Location $PSScriptRoot
try {
    if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) {
        $gitDirectory = Join-Path $env:ProgramFiles 'Git\cmd'
        if (-not (Test-Path -LiteralPath (Join-Path $gitDirectory 'git.exe'))) {
            throw 'Git is required. Install Git for Windows or add its cmd directory to PATH.'
        }
        $env:Path = "$gitDirectory;$env:Path"
    }
    if (-not (Get-Command node.exe -ErrorAction SilentlyContinue) -or
        -not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) {
        throw 'Node.js and npm are required. Install Node.js 24 and open a new PowerShell window.'
    }
    if (-not (Test-Path -LiteralPath 'node_modules\astro\package.json')) {
        throw 'Website dependencies are missing. Run npm ci in the portfolio directory, then restart this launcher.'
    }
    if ($Mode -eq 'preview' -and -not (Test-Path -LiteralPath 'dist\index.html')) {
        throw 'The production build is missing. Run npm run build first, or use the default dev mode.'
    }

    Write-Host 'Starting the local portfolio. Use the URL printed by Astro; press Ctrl+C to stop.'
    & npm.cmd run $Mode
    if ($LASTEXITCODE -ne 0) {
        throw "The portfolio server exited with code $LASTEXITCODE. See the output above."
    }
}
finally {
    $env:Path = $originalPath
    Pop-Location
}
