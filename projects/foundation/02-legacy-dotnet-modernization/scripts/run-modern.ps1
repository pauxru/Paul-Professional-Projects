Set-Location (Join-Path $PSScriptRoot "..")
dotnet run --project modern\src\Northstar.Api --launch-profile http
