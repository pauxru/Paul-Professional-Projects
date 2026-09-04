$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$py = "C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe"
if (-not (Test-Path $py)) { $py = "python" }

& $py -m unittest discover -s tests -t . -v
exit $LASTEXITCODE
