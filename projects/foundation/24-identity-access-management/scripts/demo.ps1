[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5024"
)

$ErrorActionPreference = "Stop"

function Get-IgaToken {
    param(
        [Parameter(Mandatory)] [string]$Subject,
        [Parameter(Mandatory)] [string[]]$Scopes
    )

    $body = @{ subject = $Subject; scopes = $Scopes } | ConvertTo-Json
    (Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/token" `
        -ContentType "application/json" -Body $body).accessToken
}

function Invoke-Iga {
    param(
        [Parameter(Mandatory)] [string]$Method,
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Token,
        [object]$Body
    )

    $parameters = @{
        Method  = $Method
        Uri     = "$BaseUrl$Path"
        Headers = @{ Authorization = "Bearer $Token"; "X-Correlation-Id" = "portfolio-demo" }
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 10
    }
    Invoke-RestMethod @parameters
}

Write-Host "Northstar IGA end-to-end demo" -ForegroundColor Cyan
Invoke-RestMethod "$BaseUrl/health/ready" | Out-Null

$adminToken = Get-IgaToken -Subject "portfolio-demo-admin" -Scopes @("iga.read", "iga.admin", "iga.approve")
$seedUsers = (Invoke-Iga -Method Get -Path "/api/v1/users?page=1&pageSize=100" -Token $adminToken).items
$financeManager = $seedUsers | Where-Object { $_.jobTitle -eq "Finance Manager" } | Select-Object -First 1
if ($null -eq $financeManager) {
    throw "Seeded Finance Manager was not found."
}

$suffix = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$joinerBody = @{
    employeeNumber = "DEMO-$suffix"
    displayName     = "Synthetic Demo Joiner $suffix"
    email           = "demo.joiner.$suffix@northstar.example"
    department      = "Finance"
    jobTitle        = "Finance Analyst"
    managerId       = $financeManager.id
    location        = "Nairobi"
    costCentre      = "CC-FIN"
    employmentType  = "Employee"
    clearance       = 3
    startDate       = [DateOnly]::FromDateTime([DateTime]::UtcNow).ToString("yyyy-MM-dd")
    endDate         = $null
}

Write-Host "1. Create and process a joiner" -ForegroundColor Yellow
$joiner = Invoke-Iga -Method Post -Path "/api/v1/users" -Token $adminToken -Body $joinerBody
$workflow = Invoke-Iga -Method Post -Path "/api/v1/users/$($joiner.id)/joiner" -Token $adminToken
Write-Host "   Joiner workflow: $($workflow.status)"

$entitlements = Invoke-Iga -Method Get -Path "/api/v1/entitlements" -Token $adminToken
$requestedEntitlement = $entitlements | Where-Object { $_.key -eq "finance-administer" } | Select-Object -First 1
$jitEntitlement = $entitlements | Where-Object { $_.key -eq "erp-administer" } | Select-Object -First 1
if ($null -eq $requestedEntitlement -or $null -eq $jitEntitlement) {
    throw "Required seeded privileged entitlements were not found."
}

Write-Host "2. Request high-risk access and complete manager/owner/security approval" -ForegroundColor Yellow
$requesterToken = Get-IgaToken -Subject $joiner.id -Scopes @("iga.read")
$request = Invoke-Iga -Method Post -Path "/api/v1/requests" -Token $requesterToken -Body @{
    userId        = $joiner.id
    requesterId   = $joiner.id
    targetType    = "Entitlement"
    targetId      = $requestedEntitlement.id
    justification = "Quarter-end Finance configuration work under approved change CHG-DEMO"
    durationDays  = 1
}

while ($request.status -eq "Pending") {
    $steps = Invoke-Iga -Method Get -Path "/api/v1/requests/$($request.id)/steps" -Token $adminToken
    $activeSteps = $steps | Where-Object { $_.status -eq "Pending" }
    foreach ($step in $activeSteps) {
        $approverToken = Get-IgaToken -Subject $step.approverId -Scopes @("iga.approve")
        $request = Invoke-Iga -Method Post `
            -Path "/api/v1/requests/$($request.id)/steps/$($step.id)/approve" `
            -Token $approverToken `
            -Body @{ actorId = $step.approverId; reason = "Approved during the portfolio workflow demonstration" }
    }
}
Write-Host "   Access request: $($request.status)"

Write-Host "3. Demonstrate JIT decision change and automatic expiry" -ForegroundColor Yellow
$beforeJit = Invoke-Iga -Method Post -Path "/api/v1/authz/evaluate" -Token $adminToken -Body @{
    userId      = $joiner.id
    permission  = $jitEntitlement.permission
    resource    = @{ classification = "Restricted" }
    environment = @{ networkZone = "Corporate"; deviceTrust = "Trusted"; mfaLevel = 3 }
}
$elevation = Invoke-Iga -Method Post -Path "/api/v1/elevations" -Token $adminToken -Body @{
    userId                    = $joiner.id
    entitlementId            = $jitEntitlement.id
    justification            = "Temporary ERP incident diagnostics for the portfolio demonstration"
    ticketReference          = "INC-DEMO-$suffix"
    startsAt                 = [DateTimeOffset]::UtcNow.ToString("o")
    endsAt                   = [DateTimeOffset]::UtcNow.AddSeconds(5).ToString("o")
    requiresApproval         = $false
    sessionRecordingReference = "recording://INC-DEMO-$suffix"
}
$duringJit = Invoke-Iga -Method Post -Path "/api/v1/authz/evaluate" -Token $adminToken -Body @{
    userId      = $joiner.id
    permission  = $jitEntitlement.permission
    resource    = @{ classification = "Restricted" }
    environment = @{ networkZone = "Corporate"; deviceTrust = "Trusted"; mfaLevel = 3 }
}
Start-Sleep -Seconds 6
$expired = Invoke-Iga -Method Post -Path "/api/v1/elevations/expire" -Token $adminToken
$afterJit = Invoke-Iga -Method Post -Path "/api/v1/authz/evaluate" -Token $adminToken -Body @{
    userId      = $joiner.id
    permission  = $jitEntitlement.permission
    resource    = @{ classification = "Restricted" }
    environment = @{ networkZone = "Corporate"; deviceTrust = "Trusted"; mfaLevel = 3 }
}
Write-Host "   Decision before=$($beforeJit.decision), during=$($duringJit.decision), after=$($afterJit.decision); expired=$($expired.expired)"

Write-Host "4. Create a risk-scoped campaign and enforce deadline auto-revocation" -ForegroundColor Yellow
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(8)
$campaign = Invoke-Iga -Method Post -Path "/api/v1/campaigns" -Token $adminToken -Body @{
    name         = "Portfolio high-risk certification $suffix"
    scopeType    = "risk"
    scopeValue   = "High"
    reviewerMode = "EntitlementOwner"
    deadline     = $deadline.ToString("o")
}
$itemsBefore = Invoke-Iga -Method Get -Path "/api/v1/campaigns/$($campaign.id)/items" -Token $adminToken
Write-Host "   Campaign generated $($itemsBefore.Count) effective user-entitlement review items."
$remaining = [Math]::Max(1, [Math]::Ceiling(($deadline - [DateTimeOffset]::UtcNow).TotalSeconds) + 1)
Start-Sleep -Seconds $remaining
$autoRevoked = Invoke-Iga -Method Post -Path "/api/v1/campaigns/auto-revoke-overdue" -Token $adminToken
$progress = Invoke-Iga -Method Get -Path "/api/v1/campaigns/$($campaign.id)/progress" -Token $adminToken
Write-Host "   Auto-revoked=$($autoRevoked.autoRevoked); completion=$($progress.completionPercent)%"

Write-Host "5. Show the final derivation profile and audit evidence" -ForegroundColor Yellow
$profile = Invoke-Iga -Method Get -Path "/api/v1/reports/users/$($joiner.id)/access-profile" -Token $adminToken
$audit = Invoke-Iga -Method Get -Path "/api/v1/audit?page=1&pageSize=20" -Token $adminToken
Write-Host "   Effective entitlements now: $($profile.access.Count)"
Write-Host "   Recent audit records returned: $($audit.items.Count)"
Write-Host "Demo complete." -ForegroundColor Green
