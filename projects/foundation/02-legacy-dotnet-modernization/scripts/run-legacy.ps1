Set-Location (Join-Path $PSScriptRoot "..")
dotnet run --project legacy\Northstar.Legacy.Web --launch-profile http
