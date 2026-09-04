$ErrorActionPreference = "Stop"

$base = "http://localhost:5016"
$webhookSecret = "dev-only-not-a-real-secret-webhook-key-0123456789"
$savannaCustomerId = "16000000-0000-0000-0000-000000000041"
$meteredPlanId = "16000000-0000-0000-0000-000000000030"
$meteredPlanVersionId = "16000000-0000-0000-0000-000000000031"
$apiCallsMeterId = "16000000-0000-0000-0000-000000000001"

function New-Key { [guid]::NewGuid().ToString("N") }

function Invoke-BillingPost {
    param(
        [string]$Path,
        [object]$Body,
        [hashtable]$Headers
    )

    $requestHeaders = @{} + $Headers
    $requestHeaders["Idempotency-Key"] = New-Key
    Invoke-RestMethod -Method Post -Uri "$base$Path" -Headers $requestHeaders `
        -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 12 -Compress)
}

Write-Host "1. Obtaining Development-only administrator token..."
$token = (Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/token" `
    -ContentType "application/json" `
    -Body (@{
        subject = "portfolio-demo"
        scopes = @("billing.admin")
    } | ConvertTo-Json -Compress)).accessToken
$auth = @{ Authorization = "Bearer $token" }

Write-Host "2. Appending an immutable API Scale plan version..."
$newVersion = Invoke-BillingPost -Path "/api/v1/plans/$meteredPlanId/versions" -Headers $auth -Body @{
    effectiveFrom = [DateTimeOffset]::UtcNow.AddDays(-20).ToString("O")
    currency = "USD"
    taxInclusive = $false
    pricing = @{
        model = "GraduatedWithOverage"
        flatFeeMinor = 8000
        unitPriceMinor = 3
        includedUnits = 10000
    }
}

$periodStart = [DateTimeOffset]::UtcNow.AddMonths(-1)
Write-Host "3. Subscribing Savanna Logistics Ltd (fictional)..."
$subscription = Invoke-BillingPost -Path "/api/v1/subscriptions" -Headers $auth -Body @{
    customerId = $savannaCustomerId
    planVersionId = $meteredPlanVersionId
    quantity = 1
    startAt = $periodStart.ToString("O")
    trialEndBehavior = "Activate"
}

Write-Host "4. Metering 12,000 API calls and replaying the same immutable event..."
$eventId = "demo-usage-$(New-Key)"
$usageBody = @{
    eventId = $eventId
    subscriptionId = $subscription.id
    meterId = $apiCallsMeterId
    occurredAt = [DateTimeOffset]::UtcNow.AddDays(-10).ToString("O")
    quantity = 12000
}
$firstUsage = Invoke-RestMethod -Method Post -Uri "$base/api/v1/usage" -Headers $auth `
    -ContentType "application/json" -Body ($usageBody | ConvertTo-Json -Compress)
$replayedUsage = Invoke-RestMethod -Method Post -Uri "$base/api/v1/usage" -Headers $auth `
    -ContentType "application/json" -Body ($usageBody | ConvertTo-Json -Compress)
Write-Host "   First duplicate=$($firstUsage.duplicate); replay duplicate=$($replayedUsage.duplicate)"

Write-Host "5. Previewing and applying the mid-cycle upgrade..."
$changeAt = [DateTimeOffset]::UtcNow.AddDays(-10)
$preview = Invoke-BillingPost -Path "/api/v1/invoices/preview" -Headers $auth -Body @{
    subscriptionId = $subscription.id
    proposedPlanVersionId = $newVersion.id
    proposedQuantity = 1
    asOf = $changeAt.ToString("O")
    prorationBehavior = "CreateProrations"
}
Write-Host "   Preview net minor units: $($preview.netAmountMinor)"
$null = Invoke-BillingPost -Path "/api/v1/subscriptions/$($subscription.id)/change" -Headers $auth -Body @{
    planVersionId = $newVersion.id
    quantity = 1
    changeAt = $changeAt.ToString("O")
    prorationBehavior = "CreateProrations"
}

Write-Host "6. Running invoicing twice; the period constraint prevents a duplicate..."
$run1 = Invoke-BillingPost -Path "/api/v1/invoices/run" -Headers $auth -Body @{}
$run2 = Invoke-BillingPost -Path "/api/v1/invoices/run" -Headers $auth -Body @{}
Write-Host "   Generated first=$($run1.generated), second=$($run2.generated)"
$invoices = Invoke-RestMethod -Method Get -Uri "$base/api/v1/invoices?page=1&pageSize=100" -Headers $auth
$invoice = $invoices.items | Where-Object { $_.subscriptionId -eq $subscription.id } |
    Sort-Object periodEnd -Descending | Select-Object -First 1
if (-not $invoice) { throw "Demo invoice was not generated." }
Write-Host "   Invoice $($invoice.number), total $($invoice.totalMinor) $($invoice.currency)"

Write-Host "7. Simulating insufficient funds; a day 1/3/5/7 dunning case is queued..."
$failedInvoice = Invoke-BillingPost -Path "/api/v1/invoices/$($invoice.id)/pay" -Headers $auth -Body @{
    paymentMethodToken = "pm_insufficient"
}
$dunning = Invoke-RestMethod -Method Get -Uri "$base/api/v1/invoices/$($invoice.id)/dunning" -Headers $auth
Write-Host "   Invoice remains $($failedInvoice.status); dunning case $($dunning.id), attempts=$($dunning.attemptsCompleted)."
Write-Host "   See the FakeClock tests and dunning runbook for the day 1/3/5/7 execution."

Write-Host "8. Recovering with an HMAC-signed provider success webhook..."
$rawBody = @{
    eventType = "payment.succeeded"
    invoiceId = $invoice.id
} | ConvertTo-Json -Compress
$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$message = "$timestamp.$rawBody"
$hmac = [System.Security.Cryptography.HMACSHA256]::new(
    [System.Text.Encoding]::UTF8.GetBytes($webhookSecret))
try {
    $digest = [Convert]::ToHexString(
        $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($message))).ToLowerInvariant()
}
finally {
    $hmac.Dispose()
}
$webhookHeaders = @{
    "X-Billing-Signature" = "t=$timestamp,v1=$digest"
    "X-Webhook-Nonce" = "demo-recovery-$(New-Key)"
}
$null = Invoke-RestMethod -Method Post -Uri "$base/api/v1/webhooks/payments" `
    -Headers $webhookHeaders -ContentType "application/json" -Body $rawBody

$recovered = Invoke-RestMethod -Method Get -Uri "$base/api/v1/invoices/$($invoice.id)" -Headers $auth
Write-Host "   Recovered invoice status: $($recovered.status)"
Write-Host "9. HTML invoice: $base/api/v1/invoices/$($invoice.id)/render?format=html"
Write-Host "Demo complete. All entities are fictional and all adapters are local simulators."
