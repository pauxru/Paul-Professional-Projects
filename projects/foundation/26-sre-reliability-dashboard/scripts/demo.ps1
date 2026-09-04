<#
Runs against a Development instance on http://localhost:5026.
It uses only synthetic Northstar Group (fictional) data.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5026"
)

$ErrorActionPreference = "Stop"

function Invoke-NorthstarApi {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [ValidateSet("Get", "Post")] [string]$Method = "Get",
        [object]$Body
    )

    $parameters = @{
        Uri = "$BaseUrl/api/v1$Path"
        Method = $Method
        Headers = $script:Headers
        ContentType = "application/json"
    }
    if ($null -ne $Body) {
        $parameters.Body = $Body | ConvertTo-Json -Depth 8
    }
    Invoke-RestMethod @parameters
}

Write-Host "Checking API health at $BaseUrl..."
Invoke-RestMethod "$BaseUrl/health/ready" | Out-Null

$token = Invoke-RestMethod "$BaseUrl/api/v1/auth/token" -Method Post -ContentType "application/json" -Body (@{
    subject = "demo.sre@northstar.invalid"
    scopes = @("reliability.read", "reliability.write", "reliability.admin")
} | ConvertTo-Json)
$script:Headers = @{ Authorization = "Bearer $($token.accessToken)" }

$slos = Invoke-NorthstarApi "/slos"
$checkoutSlo = $slos | Where-Object serviceSlug -eq "checkout" | Select-Object -First 1
if ($null -eq $checkoutSlo) {
    throw "Seeded checkout SLO was not found. Start the API in Development first."
}

Write-Host "`n1. Initial deployment gate"
Invoke-NorthstarApi "/gates/checkout/deploy" | Format-List

$now = [DateTimeOffset]::UtcNow
$incidentInjection = @{
    kind = "PartialOutage"
    startsAt = $now.AddHours(-1).ToString("o")
    endsAt = $now.AddMinutes(2).ToString("o")
    severity = 1.0
}
Write-Host "`n2. Simulating diurnal traffic with a partial checkout outage"
$simulation = Invoke-NorthstarApi "/simulator/run" "Post" @{
    serviceSlug = "checkout"
    startsAt = $now.AddHours(-1).ToString("o")
    endsAt = $now.AddMinutes(2).ToString("o")
    resolutionMinutes = 1
    baseRequestsPerMinute = 1000
    endpoint = "/api/checkout"
    region = "eu-west"
    tier = "Tier0"
    backgroundErrorRate = 0.0002
    incident = $incidentInjection
    randomSeed = 26026
}
Write-Host "Generated $($simulation.generated) synthetic metric samples."

Write-Host "`n3. Evaluating multi-window burn-rate alerts"
$alerts = Invoke-NorthstarApi "/alerts/evaluate?sloId=$($checkoutSlo.id)" "Post"
$fastPage = $alerts | Where-Object ruleName -eq "fast-page" | Select-Object -First 1
$fastPage | Format-List ruleName, state, longBurnRate, shortBurnRate, detectionLag, suppressionReason

Write-Host "`n4. Gate after budget burn"
Invoke-NorthstarApi "/gates/checkout/deploy" | Format-List

Write-Host "`n5. Declaring, mitigating, and resolving the incident"
$incident = Invoke-NorthstarApi "/incidents" "Post" @{
    title = "Synthetic checkout partial outage"
    severity = "Sev1"
    affectedServices = @("checkout")
    commander = "Asha Commander"
    communicationsLead = "Nia Communications"
    startedAt = $now.AddMinutes(-20).ToString("o")
    author = "demo.sre@northstar.invalid"
    linkedAlertIds = @($fastPage.id)
}
Invoke-NorthstarApi "/incidents/$($incident.id)/mitigate" "Post" @{
    author = "Asha Commander"
    message = "Synthetic rollback completed; monitoring recovery."
} | Out-Null
$resolved = Invoke-NorthstarApi "/incidents/$($incident.id)/resolve" "Post" @{
    author = "Asha Commander"
    message = "Synthetic traffic and probes are stable."
}
$resolved | Select-Object id, status, statusPageState, budgetImpacts | Format-List

Write-Host "`n6. Creating a blameless postmortem with an owned action"
$postmortem = Invoke-NorthstarApi "/postmortems" "Post" @{
    incidentId = $resolved.id
    title = "Synthetic checkout outage postmortem"
    summary = "A simulated partial outage validated alerting, command, and budget attribution paths."
    contributingFactors = @("Synthetic dependency fault exercise", "Runbook rehearsal")
    whatWentWell = "The long and short windows confirmed active burn."
    whatWentPoorly = "The synthetic exercise still required manual incident declaration."
}
$postmortem = Invoke-NorthstarApi "/postmortems/$($postmortem.id)/actions" "Post" @{
    description = "Review checkout dependency fallback coverage"
    owner = "Commerce Core"
    dueDate = $now.AddDays(14).ToString("yyyy-MM-dd")
}
Invoke-NorthstarApi "/postmortems/$($postmortem.id)/submit-review" "Post" | Out-Null
Invoke-NorthstarApi "/postmortems/$($postmortem.id)/approve" "Post" | Out-Null
Invoke-NorthstarApi "/postmortems/$($postmortem.id)/publish" "Post" | Out-Null

Write-Host "`n7. Alert quality and final gate"
Invoke-NorthstarApi "/reports/alert-quality" | Format-List
Invoke-NorthstarApi "/gates/checkout/deploy" | Format-List
Write-Host "`nDemo complete. Open $BaseUrl/ to inspect SVG dashboard data."
