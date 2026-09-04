#requires -Version 7.0
<#
.SYNOPSIS
End-to-end demo script for the Contoso Payments API.

.DESCRIPTION
Runs against a live API on http://localhost:5001. Drives the full happy path
(product list -> order -> authorize -> capture -> refund -> reconciliation)
and two failure paths (idempotency conflict; refund exceeding captured).

Run in a second shell after starting the API with:
    dotnet run --project src\Contoso.Payments.Api -c Release

.NOTES
Fictional demo data only. No real customers, providers, or funds.
#>

$ErrorActionPreference = 'Stop'
$BaseUrl = 'http://localhost:5001'

function Section($msg) {
    Write-Host ""
    Write-Host "==== $msg ====" -ForegroundColor Cyan
}

function Post-Json($path, $body, $headers) {
    return Invoke-RestMethod -Method Post -Uri "$BaseUrl$path" `
        -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 8)
}

# ------------- 1. Mint a token -------------
Section 'Mint dev token'
$tokenBody = @{ subject = 'demo'; scopes = @('orders:write', 'admin', 'reconciliation:run'); lifetimeMinutes = 60 }
$tokenResp = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/token" `
    -ContentType 'application/json' -Body ($tokenBody | ConvertTo-Json)
$token = $tokenResp.accessToken
$authHeaders = @{ Authorization = "Bearer $token" }
Write-Host "  Token type: $($tokenResp.tokenType); expires in $($tokenResp.expiresInSeconds) sec"

# ------------- 2. List catalog -------------
Section 'List catalog'
$products = Invoke-RestMethod -Uri "$BaseUrl/api/v1/products"
$products.items | Format-Table sku, name, price, currency -AutoSize

# Pick a USD SKU deterministically.
$usdSku = ($products.items | Where-Object { $_.currency -eq 'USD' } | Select-Object -First 1).sku
if (-not $usdSku) { throw 'No USD SKU seeded.' }
Write-Host "  Using SKU: $usdSku"

# ------------- 3. Happy path: place order -------------
Section 'Place order (idempotency-key: k1)'
$k1 = [guid]::NewGuid().ToString()
$orderBody = @{ customerRef = 'cust-demo'; currency = 'USD'; lines = @(@{ sku = $usdSku; quantity = 2 }) }
$hdrs = $authHeaders + @{ 'Idempotency-Key' = $k1 }
$order = Post-Json '/api/v1/orders' $orderBody $hdrs
Write-Host "  Order id: $($order.id), status: $($order.status), total: $($order.total)"

# ------------- 4. Replay same key -------------
Section 'Replay same key (should return same order + Idempotent-Replay: true)'
$req2 = Invoke-WebRequest -Method Post -Uri "$BaseUrl/api/v1/orders" `
    -Headers ($hdrs + @{ 'Content-Type' = 'application/json' }) `
    -Body ($orderBody | ConvertTo-Json)
$replayHdr = $req2.Headers['Idempotent-Replay']
Write-Host "  Idempotent-Replay: $replayHdr (expected: true)"

# ------------- 5. Idempotency conflict -------------
Section 'Idempotency conflict (same key, different body)'
$bodyB = @{ customerRef = 'cust-different'; currency = 'USD'; lines = @(@{ sku = $usdSku; quantity = 1 }) }
try {
    Post-Json '/api/v1/orders' $bodyB $hdrs | Out-Null
    Write-Host '  UNEXPECTED — server accepted mismatched body.' -ForegroundColor Red
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Write-Host "  Expected 409; got $code" -ForegroundColor Green
}

# ------------- 6. Authorize a payment -------------
Section 'Authorize payment'
$payKey = [guid]::NewGuid().ToString()
$intent = Post-Json '/api/v1/payments/authorize' @{ orderId = $order.id } ($authHeaders + @{ 'Idempotency-Key' = $payKey })
Write-Host "  Payment intent id: $($intent.id), status: $($intent.status)"

# ------------- 7. Capture -------------
Section 'Capture payment'
$captured = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/payments/$($intent.id)/capture" -Headers $authHeaders
Write-Host "  Captured status: $($captured.status)"

# ------------- 8. Partial refund -------------
Section 'Partial refund'
$refundKey = [guid]::NewGuid().ToString()
$refund = Post-Json '/api/v1/refunds' @{ orderId = $order.id; amount = 3.00; reason = 'demo partial' } ($authHeaders + @{ 'Idempotency-Key' = $refundKey })
Write-Host "  Refund id: $($refund.id), amount: $($refund.amount)"

# ------------- 9. Failure path: refund exceeding captured -------------
Section 'Refund exceeding captured (should return 422)'
try {
    Post-Json '/api/v1/refunds' @{ orderId = $order.id; amount = 999.99; reason = 'over' } ($authHeaders + @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }) | Out-Null
    Write-Host '  UNEXPECTED — refund accepted.' -ForegroundColor Red
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Write-Host "  Expected 422; got $code" -ForegroundColor Green
}

# ------------- 10. Health -------------
Section 'Health checks'
$live = Invoke-WebRequest -Uri "$BaseUrl/health/live" -SkipHttpErrorCheck
$ready = Invoke-WebRequest -Uri "$BaseUrl/health/ready" -SkipHttpErrorCheck
Write-Host "  /health/live => $($live.StatusCode)"
Write-Host "  /health/ready => $($ready.StatusCode)"

Section 'Demo complete'
