param(
    [string]$BaseUrl = 'http://localhost:5018'
)

$ErrorActionPreference = 'Stop'
$token = (Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/token" -ContentType 'application/json' -Body (@{
    actor = 'demo-operator'
    scopes = @('flags:read', 'flags:write', 'flags:approve')
} | ConvertTo-Json)).accessToken
$headers = @{ Authorization = "Bearer $token" }

Write-Host "Flags in dev:" -ForegroundColor Cyan
Invoke-RestMethod -Uri "$BaseUrl/api/v1/projects/acme/environments/dev/flags" -Headers $headers | Format-Table key, valueType, isOn, lifecycleStatus

Write-Host "`nTargeting preview:" -ForegroundColor Cyan
$preview = @{ flagKey = 'new-checkout'; contextKey = 'demo-user'; attributes = @{ country = 'KE' } } | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/projects/acme/environments/dev/evaluate" -Headers $headers -ContentType 'application/json' -Body $preview | ConvertTo-Json -Depth 5

Write-Host "`nEmergency-style dev kill switch (then restore):" -ForegroundColor Cyan
$off = @{ enabled = $false; comment = 'Demo only'; ticketReference = 'DEMO-18' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/projects/acme/environments/dev/flags/new-checkout/kill-switch" -Headers $headers -ContentType 'application/json' -Body $off | Out-Null
Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/projects/acme/environments/dev/evaluate" -Headers $headers -ContentType 'application/json' -Body $preview | ConvertTo-Json -Depth 5
$on = @{ enabled = $true; comment = 'Restore after demo'; ticketReference = 'DEMO-18' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/projects/acme/environments/dev/flags/new-checkout/kill-switch" -Headers $headers -ContentType 'application/json' -Body $on | Out-Null

Write-Host "`nOpen $BaseUrl/ for the admin UI and start samples\DemoApp for a live SDK consumer." -ForegroundColor Green
