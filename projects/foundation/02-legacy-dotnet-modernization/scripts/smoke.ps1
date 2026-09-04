Set-Location (Join-Path $PSScriptRoot "..")
$token = (Invoke-RestMethod http://localhost:5002/api/v1/auth/token -Method Post -ContentType "application/json" -Body '{"subject":"smoke","scopes":["claims:read"]}').accessToken
$headers = @{ Authorization = "Bearer $token" }
Invoke-RestMethod http://localhost:5002/health/live
Invoke-RestMethod http://localhost:5002/api/v1/claims -Headers $headers
