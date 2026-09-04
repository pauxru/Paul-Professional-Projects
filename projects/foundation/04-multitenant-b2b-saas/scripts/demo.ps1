param(
    [string]$BaseUrl = "http://localhost:5004"
)

$ErrorActionPreference = "Stop"

function Get-DemoSession {
    param([string]$Email, [string]$Tenant)
    $body = @{ email = $Email; tenantSlug = $Tenant } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/token" -ContentType "application/json" -Body $body
}

function Get-Headers {
    param($Session, [string]$Tenant)
    @{
        Authorization = "Bearer $($Session.accessToken)"
        "X-Tenant" = $Tenant
        "X-Correlation-Id" = "demo-$([guid]::NewGuid().ToString('N'))"
    }
}

Write-Host "FieldOps SaaS demonstration" -ForegroundColor Cyan
$savanna = Get-DemoSession -Email "owner@fieldops.demo" -Tenant "savanna-logistics"
$jua = Get-DemoSession -Email "owner@fieldops.demo" -Tenant "jua-kali-manufacturing"
$savannaHeaders = Get-Headers -Session $savanna -Tenant "savanna-logistics"
$juaHeaders = Get-Headers -Session $jua -Tenant "jua-kali-manufacturing"

Write-Host "`n1. Tenant-scoped usage and jobs"
Invoke-RestMethod -Uri "$BaseUrl/api/v1/usage" -Headers $savannaHeaders | ConvertTo-Json -Depth 6
$juaJobs = Invoke-RestMethod -Uri "$BaseUrl/api/v1/jobs?page=1&pageSize=5" -Headers $juaHeaders
$foreignJobId = $juaJobs.items[0].id

Write-Host "`n2. Cross-tenant IDOR attempt (expected HTTP 404)"
try {
    Invoke-RestMethod -Uri "$BaseUrl/api/v1/jobs/$foreignJobId" -Headers $savannaHeaders
    throw "Isolation demonstration failed: foreign job was visible."
}
catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    Write-Host "PASS: Savanna could not read Jua Kali job $foreignJobId." -ForegroundColor Green
}

Write-Host "`n3. Feature-flag evaluation"
Invoke-RestMethod -Uri "$BaseUrl/api/v1/feature-flags/mobile-inspections/evaluate" -Headers $savannaHeaders |
    ConvertTo-Json

Write-Host "`n4. Generate and deliver a signed payment-failure webhook"
$simulated = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/billing/simulator/webhook" `
    -Headers $savannaHeaders -ContentType "application/json" `
    -Body (@{ type = "PaymentFailed"; eventId = "evt_demo_$([guid]::NewGuid().ToString('N'))" } | ConvertTo-Json)
Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/billing/webhooks/simulator" `
    -Headers @{ "X-FieldOps-Signature" = $simulated.signature } `
    -ContentType "application/json" -Body $simulated.rawBody | ConvertTo-Json

Write-Host "`n5. Open the dashboard: $BaseUrl/" -ForegroundColor Cyan
