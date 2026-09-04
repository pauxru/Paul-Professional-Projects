# scripts/demo.ps1 — run this after `dotnet run --project src\NotificationPlatform.Api -c Release`
# starts listening on http://localhost:5011.

$ErrorActionPreference = 'Stop'
$base = 'http://localhost:5011'
$tenant = '11111111-1111-1111-1111-111111111111'  # Contoso Retail (fictional)

Write-Host "== 1. Health"
Invoke-RestMethod "$base/health/live"

Write-Host "== 2. Issue token"
$body = @{ tenantId = $tenant } | ConvertTo-Json
$token = (Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/token" `
    -ContentType 'application/json' -Body $body).access_token
$headers = @{ Authorization = "Bearer $token" }

Write-Host "== 3. Send transactional email"
$req = @{
  templateKey = "order.confirmation"; channel = "Email"
  recipientExternalId = "cust-001"
  payload = @{ user=@{firstName="Alice"}; order=@{id="SO-1042"; item="Coffee"; total="KES 2,500"} }
  priority = "Transactional"; category = "Transactional"
  idempotencyKey = "demo-1"
} | ConvertTo-Json -Depth 5
$outcome = Invoke-RestMethod -Method Post -Uri "$base/api/v1/notifications" `
    -Headers $headers -ContentType 'application/json' -Body $req
$outcome | ConvertTo-Json -Depth 5

Write-Host "== 4. Idempotent replay (same key)"
$replay = Invoke-RestMethod -Method Post -Uri "$base/api/v1/notifications" `
    -Headers $headers -ContentType 'application/json' -Body $req
$replay | ConvertTo-Json -Depth 5

Write-Host "== 5. List notifications"
Invoke-RestMethod -Uri "$base/api/v1/notifications?page=1&pageSize=5" -Headers $headers |
    ConvertTo-Json -Depth 6

Write-Host "== 6. Analytics summary"
Invoke-RestMethod -Uri "$base/api/v1/analytics/summary" -Headers $headers |
    ConvertTo-Json -Depth 5

Write-Host "== 7. DLQ (should be empty on a clean run)"
Invoke-RestMethod -Uri "$base/api/v1/admin/dlq?page=1&pageSize=5" -Headers $headers |
    ConvertTo-Json -Depth 5

Write-Host ""
Write-Host "Demo complete.  Open http://localhost:5011/swagger for the full API."
