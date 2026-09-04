# Demo script — Enterprise Order & Payments Platform

Runs against a live API on port 5001. Assumes you have the dev token endpoint
open (the default in Development).

## Prep

```powershell
cd 01-enterprise-order-payments-platform
dotnet run --project src\Contoso.Payments.Api -c Release
# In another shell:
$body = '{"subject":"demo","scopes":["orders:write","admin","reconciliation:run"],"lifetimeMinutes":60}'
$t = (Invoke-RestMethod -Method Post -Uri http://localhost:5001/api/v1/auth/token `
    -ContentType 'application/json' -Body $body).accessToken
$h = @{ Authorization = "Bearer $t" }
```

## Happy path

1. **List catalog** (public).
   ```powershell
   Invoke-RestMethod http://localhost:5001/api/v1/products
   ```

2. **Place order — with idempotency key.**
   ```powershell
   $orderKey = [guid]::NewGuid()
   $order = @{ customerRef = "cust-demo"; currency = "USD";
       lines = @(@{ sku = "COFFEE-01"; quantity = 2 }) } | ConvertTo-Json
   $orderResp = Invoke-RestMethod -Method Post -Uri http://localhost:5001/api/v1/orders `
       -Headers ($h + @{ 'Idempotency-Key' = $orderKey }) `
       -ContentType 'application/json' -Body $order
   $orderResp
   ```

3. **Replay the same POST — get the same response body plus `Idempotent-Replay: true`.**
   ```powershell
   Invoke-WebRequest -Method Post -Uri http://localhost:5001/api/v1/orders `
       -Headers ($h + @{ 'Idempotency-Key' = $orderKey; 'Content-Type' = 'application/json' }) `
       -Body $order | Format-List Headers, StatusCode
   # Idempotent-Replay: true should appear in the response headers.
   ```

4. **Authorize a payment intent.**
   ```powershell
   $payKey = [guid]::NewGuid()
   $authBody = @{ orderId = $orderResp.id } | ConvertTo-Json
   $intent = Invoke-RestMethod -Method Post -Uri http://localhost:5001/api/v1/payments/authorize `
       -Headers ($h + @{ 'Idempotency-Key' = $payKey }) `
       -ContentType 'application/json' -Body $authBody
   $intent   # status should be Authorized (or Requires under AsynchronousPending)
   ```

5. **Capture and refund a portion.**
   ```powershell
   Invoke-RestMethod -Method Post -Uri "http://localhost:5001/api/v1/payments/$($intent.id)/capture" -Headers $h
   $refundBody = @{ orderId = $orderResp.id; amount = 3.00; reason = "demo partial refund" } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri http://localhost:5001/api/v1/refunds `
       -Headers ($h + @{ 'Idempotency-Key' = ([guid]::NewGuid()) }) `
       -ContentType 'application/json' -Body $refundBody
   ```

## Failure paths

### Idempotency conflict — same key, different body

```powershell
$key = [guid]::NewGuid()
$bodyA = @{ customerRef = "A"; currency = "USD"; lines = @(@{ sku = "COFFEE-01"; quantity = 1 }) } | ConvertTo-Json
$bodyB = @{ customerRef = "B"; currency = "USD"; lines = @(@{ sku = "COFFEE-01"; quantity = 1 }) } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5001/api/v1/orders `
    -Headers ($h + @{ 'Idempotency-Key' = $key }) -ContentType 'application/json' -Body $bodyA
try {
    Invoke-RestMethod -Method Post -Uri http://localhost:5001/api/v1/orders `
        -Headers ($h + @{ 'Idempotency-Key' = $key }) -ContentType 'application/json' -Body $bodyB
} catch {
    Write-Host "Expected 409 Conflict — got status $($_.Exception.Response.StatusCode.value__)"
}
```

### Refund exceeding captured amount → 422 ProblemDetails

```powershell
$refundBig = @{ orderId = $orderResp.id; amount = 999.99; reason = "over-refund" } | ConvertTo-Json
try {
    Invoke-RestMethod -Method Post -Uri http://localhost:5001/api/v1/refunds `
        -Headers ($h + @{ 'Idempotency-Key' = ([guid]::NewGuid()) }) `
        -ContentType 'application/json' -Body $refundBig
} catch {
    Write-Host "Expected 422 — got status $($_.Exception.Response.StatusCode.value__)"
}
```

### Webhook with bad signature → 401

```powershell
$payload = '{"eventType":"payment.captured"}'
Invoke-WebRequest -Method Post -Uri http://localhost:5001/api/v1/webhooks/payments `
    -Body $payload -ContentType 'application/json' `
    -Headers @{ 'X-Contoso-Signature' = 't=0,v1=deadbeef' } -SkipHttpErrorCheck | Format-List StatusCode
```

### Reconciliation with a synthetic mismatch file

```powershell
# Use the SettlementFileGenerator from the test suite — or write your own CSV.
# The `docs/portfolio/demo-script.md` includes a manual CSV example.
```

Refer to `scripts/demo.ps1` for a self-contained script that runs the
happy-path + one failure-path end-to-end.
