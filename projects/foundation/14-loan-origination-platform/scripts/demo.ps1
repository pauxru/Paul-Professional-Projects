[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5014"
)

$ErrorActionPreference = "Stop"

function Invoke-LoanApi {
    param(
        [Parameter(Mandatory)][string]$Path,
        [ValidateSet("GET", "POST")][string]$Method = "GET",
        [object]$Body,
        [hashtable]$Headers = @{}
    )

    $uri = "$BaseUrl$Path"
    if ($PSBoundParameters.ContainsKey("Body")) {
        return Invoke-RestMethod -Uri $uri -Method $Method -Headers $Headers `
            -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 12)
    }

    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $Headers
}

Write-Host "Obtaining Development-only demo token..."
$token = Invoke-LoanApi -Path "/api/v1/auth/token" -Method POST -Body @{
    subject = "demo-credit-ops"
    scopes = @("loans:apply", "loans:underwrite", "loans:approve", "loans:admin")
}
$headers = @{
    Authorization = "Bearer $($token.accessToken)"
    "X-Correlation-Id" = "loan-demo-$([guid]::NewGuid().ToString('N'))"
}

$customer = (Invoke-LoanApi -Path "/api/v1/customers?page=1&pageSize=50" -Headers $headers).items |
    Where-Object { $_.legalName -eq "Jua Kali Manufacturing Ltd (fictional)" } |
    Select-Object -First 1
if ($null -eq $customer) {
    throw "Expected Development seed customer was not found. Start the API in Development."
}

Write-Host "1/7 Create application and submit it..."
$application = Invoke-LoanApi -Path "/api/v1/applications" -Method POST -Headers $headers -Body @{
    customerId = $customer.id
    productCode = "SME-FLEX"
    productVersion = 1
    requestedPrincipal = 100000
    requestedTermMonths = 12
    currency = "KES"
}
$application = Invoke-LoanApi -Path "/api/v1/applications/$($application.id)/submit" -Method POST -Headers $headers

Write-Host "2/7 Upload and verify mandatory synthetic documents..."
foreach ($documentType in @("NationalId", "BankStatement")) {
    $uploaded = Invoke-LoanApi -Path "/api/v1/applications/$($application.id)/documents" -Method POST -Headers $headers -Body @{
        documentType = $documentType
        fileName = "$documentType.pdf"
        contentType = "application/pdf"
        base64Content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("synthetic $documentType"))
    }
    $document = $uploaded.documents | Select-Object -Last 1
    $null = Invoke-LoanApi -Path "/api/v1/applications/$($application.id)/documents/$($document.id)/verify" -Method POST -Headers $headers -Body @{
        status = "Verified"
        reason = "Synthetic demo review."
    }
}
$application = Invoke-LoanApi -Path "/api/v1/applications/$($application.id)/documents/complete" -Method POST -Headers $headers

Write-Host "3/7 Run deterministic KYC..."
$kyc = Invoke-LoanApi -Path "/api/v1/applications/$($application.id)/kyc" -Method POST -Headers $headers
Write-Host "KYC outcome: $($kyc.providerResult.outcome); attempts: $($kyc.attempts)"

Write-Host "4/7 Run automated rules/affordability/scorecard decision (refers to underwriting queue)..."
$application = Invoke-LoanApi -Path "/api/v1/applications/$($application.id)/decision" -Method POST -Headers $headers
Write-Host "Rules decision: $($application.decisionTrace.decision); workflow stage: $($application.stage)"
$application.decisionTrace.rules | Select-Object ruleId, ruleVersion, matched, reason | Format-Table

Write-Host "5/7 Claim and approve under delegated authority..."
$null = Invoke-LoanApi -Path "/api/v1/underwriting/queue/$($application.id)/claim" -Method POST -Headers $headers
$application = Invoke-LoanApi -Path "/api/v1/underwriting/queue/$($application.id)/decision" -Method POST -Headers $headers -Body @{
    decision = "APPROVE"
    approvedPrincipal = 100000
    annualRate = 18
    termMonths = 12
    reason = "Synthetic demonstration approval after trace review."
    decisionMakerRole = "Senior"
}

Write-Host "6/7 Generate and accept immutable offer..."
$offer = Invoke-LoanApi -Path "/api/v1/offers" -Method POST -Headers $headers -Body @{
    applicationId = $application.id
    principal = 100000
    annualRate = 18
    termMonths = 12
    validityDays = 7
}
$offer = Invoke-LoanApi -Path "/api/v1/offers/$($offer.id)/accept" -Method POST -Headers $headers
Write-Host "Offer $($offer.id) accepted; schedule contains $($offer.schedule.Count) installments."

Write-Host "7/7 Request idempotent simulated disbursement..."
$disbursement = Invoke-LoanApi -Path "/api/v1/disbursements" -Method POST -Headers $headers -Body @{
    offerId = $offer.id
    providerReference = "demo-transfer-$([guid]::NewGuid().ToString('N'))"
    rail = "BankTransfer"
}
Write-Host "Disbursement: $($disbursement.status); reconciled: $($disbursement.isReconciled)"

$record = Invoke-LoanApi -Path "/api/v1/applications/$($application.id)/decision-record" -Headers $headers
Write-Host "Decision record retained product v$($record.productVersion), ruleset $($record.rulesetId) v$($record.rulesetVersion), scorecard $($record.scorecardVersion)."
