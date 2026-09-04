# Demo Script — Example Bank Digital Banking Ledger

This is a 5–8 minute spoken walkthrough for the self-directed engineering case study. The automated version is `scripts\demo.ps1`.

## 0. Setup

Spoken framing:

> This is Example Bank, a fictional banking ledger. The rule is simple: it must be impossible to create money. I will show account creation, funding, transfer, holds, reversal, FX conversion, integrity verification, and finally the concurrency tests that try to break the ledger.

Start the API:

```powershell
Set-Location C:\Users\rukwaropaul\Downloads\DEV\Projects\13-digital-banking-ledger
dotnet run --project src\ExampleBank.Ledger.Api --urls http://localhost:5013
```

The API runs at:

```text
http://localhost:5013
```

Mint a local development token. Development tokens are available only when `Auth:EnableDevTokens=true`, which is enabled in Development/Testing.

```powershell
$base = 'http://localhost:5013'
$tokenResponse = Invoke-RestMethod -Method Post -Uri "$base/api/v1/dev/token" -ContentType 'application/json' -Body '{"subject":"demo","scopes":["ledger:read","ledger:post","ledger:adjust","ledger:admin"]}'
$token = $tokenResponse.access_token
$auth = @{ Authorization = "Bearer $token" }
```

After creating accounts, keep their returned `id` values. Most posting endpoints use account ids rather than account codes.

Equivalent curl:

```bash
curl -s -X POST http://localhost:5013/api/v1/dev/token \
  -H "Content-Type: application/json" \
  -d '{"subject":"demo","scopes":["ledger:read","ledger:post","ledger:adjust","ledger:admin"]}'
```

## 1. Create Two Customer Accounts

Spoken framing:

> Customer deposit accounts are liabilities. They are credit-normal because the bank owes this money to the customer.

```powershell
$alice = Invoke-RestMethod -Method Post -Uri "$base/api/v1/accounts" -Headers ($auth + @{ 'Idempotency-Key' = 'account-alice-demo' }) -ContentType 'application/json' -Body '{
  "code":"CUST-ALICE-KES",
  "name":"Alice Demo Current Account",
  "type":"Liability",
  "currency":"KES",
  "parentCode":"DEPOSITS-KES",
  "isCustomerAccount":true
}'

$bob = Invoke-RestMethod -Method Post -Uri "$base/api/v1/accounts" -Headers ($auth + @{ 'Idempotency-Key' = 'account-bob-demo' }) -ContentType 'application/json' -Body '{
  "code":"CUST-BOB-KES",
  "name":"Bob Demo Current Account",
  "type":"Liability",
  "currency":"KES",
  "parentCode":"DEPOSITS-KES",
  "isCustomerAccount":true
}'

$accounts = Invoke-RestMethod -Method Get -Uri "$base/api/v1/accounts" -Headers $auth
$cashKes = $accounts | Where-Object Code -eq 'CASH-KES'
```

Curl:

```bash
curl -X POST http://localhost:5013/api/v1/accounts \
  -H "Authorization: Bearer $TOKEN" \
  -H "Idempotency-Key: account-alice-demo" \
  -H "Content-Type: application/json" \
  -d '{"code":"CUST-ALICE-KES","name":"Alice Demo Current Account","type":"Liability","currency":"KES","parentCode":"DEPOSITS-KES","isCustomerAccount":true}'
```

## 2. Fund One Account with a Balanced Journal Entry

Spoken framing:

> Funding Alice is not a balance update. It is a journal entry with equal debits and credits. The asset side and liability side move together.

```powershell
$fundingBody = "{
  `"type`":`"Adjustment`",
  `"description`":`"Fictional opening funding for demo`",
  `"valueDate`":`"2026-09-03`",
  `"reference`":`"demo-funding-001`",
  `"postings`":[
    {`"accountId`":`"$($cashKes.id)`",`"direction`":`"Debit`",`"amountMinor`":100000,`"currency`":`"KES`"},
    {`"accountId`":`"$($alice.id)`",`"direction`":`"Credit`",`"amountMinor`":100000,`"currency`":`"KES`"}
  ]
}"

$funding = Invoke-RestMethod -Method Post -Uri "$base/api/v1/entries" -Headers ($auth + @{ 'Idempotency-Key' = 'fund-alice-kes-demo-001'; 'X-Correlation-ID' = 'demo-funding-001' }) -ContentType 'application/json' -Body $fundingBody
```

## 3. Transfer Between the Accounts

Spoken framing:

> A transfer checks available balance and overdraft rules, then records postings. It is idempotent by client key so a retried request cannot double-post.

```powershell
$transferBody = "{
  `"fromAccountId`":`"$($alice.id)`",
  `"toAccountId`":`"$($bob.id)`",
  `"amountMinor`":25000,
  `"currency`":`"KES`",
  `"description`":`"Demo transfer from Alice to Bob`",
  `"reference`":`"demo-transfer-001`"
}"

$transfer = Invoke-RestMethod -Method Post -Uri "$base/api/v1/transfers" -Headers ($auth + @{ 'Idempotency-Key' = 'alice-to-bob-demo-001'; 'X-Correlation-ID' = 'demo-transfer-001' }) -ContentType 'application/json' -Body $transferBody
```

Curl:

```bash
curl -X POST http://localhost:5013/api/v1/transfers \
  -H "Authorization: Bearer $TOKEN" \
  -H "Idempotency-Key: alice-to-bob-demo-001" \
  -H "Content-Type: application/json" \
  -d '{"fromAccountId":"<alice-account-guid>","toAccountId":"<bob-account-guid>","amountMinor":25000,"currency":"KES","description":"Demo transfer from Alice to Bob","reference":"demo-transfer-001"}'
```

## 4. Show Balances Derive from Postings

Spoken framing:

> The API can return account balances, but the design point is that balances are derivable from the posting history. The ledger is not trusting a mutable number without an audit trail.

```powershell
Invoke-RestMethod -Method Get -Uri "$base/api/v1/accounts/$($alice.id)/balance" -Headers $auth
Invoke-RestMethod -Method Get -Uri "$base/api/v1/accounts/$($bob.id)/balance" -Headers $auth
```

## 5. Show Trial Balance Sums to Zero

Spoken framing:

> The global trial balance is the accounting smoke test. Across all accounts and currencies, the ledger should sum to zero.

```powershell
Invoke-RestMethod -Method Get -Uri "$base/api/v1/reports/trial-balance" -Headers $auth | ConvertTo-Json -Depth 10
```

Curl:

```bash
curl -s http://localhost:5013/api/v1/reports/trial-balance \
  -H "Authorization: Bearer $TOKEN"
```

## 6. Place and Capture a Hold

Spoken framing:

> Holds model authorizations. They reduce available balance without immediately becoming a final ledger movement. A hold can be fully captured, partially captured, released, or expired by a background service using `IClock`.

```powershell
$holdBody = "{
  `"accountId`":`"$($bob.id)`",
  `"amountMinor`":5000,
  `"currency`":`"KES`",
  `"expiresInMinutes`":1440,
  `"reference`":`"demo-hold-001`"
}"

$hold = Invoke-RestMethod -Method Post -Uri "$base/api/v1/holds" -Headers ($auth + @{ 'Idempotency-Key' = 'bob-hold-demo-001' }) -ContentType 'application/json' -Body $holdBody

$captureBody = "{
  `"destinationAccountId`":`"$($cashKes.id)`",
  `"captureMinor`":3000,
  `"reference`":`"Partial capture for demo hold`"
}"

Invoke-RestMethod -Method Post -Uri "$base/api/v1/holds/$($hold.id)/capture" -Headers ($auth + @{ 'Idempotency-Key' = 'bob-hold-capture-demo-001' }) -ContentType 'application/json' -Body $captureBody
```

## 7. Do a Partial Reversal

Spoken framing:

> Reversals are how the ledger corrects mistakes without deleting history. A partial reversal references the original entry and cannot exceed what was originally posted.

```powershell
Invoke-RestMethod -Method Post -Uri "$base/api/v1/reversals" -Headers ($auth + @{ 'Idempotency-Key' = 'partial-reversal-demo-001' }) -ContentType 'application/json' -Body "{
  `"originalEntryId`":`"$($transfer.id)`",
  `"amountMinor`":5000,
  `"reason`":`"Partial demo reversal`",
  `"correlationId`":`"demo-reversal-001`"
}"
```

## 8. Run an FX Conversion

Spoken framing:

> FX conversion is not just subtracting one currency and adding another. The design routes through FX clearing and rounding gain/loss accounts so value is conserved and rounding remainder is explicit.

Create a USD customer account if needed:

```powershell
$aliceUsd = Invoke-RestMethod -Method Post -Uri "$base/api/v1/accounts" -Headers ($auth + @{ 'Idempotency-Key' = 'account-alice-usd-demo' }) -ContentType 'application/json' -Body '{
  "code":"CUST-ALICE-USD",
  "name":"Alice Demo USD Account",
  "type":"Liability",
  "currency":"USD",
  "parentCode":"DEPOSITS-USD",
  "isCustomerAccount":true
}'
```

Run the conversion:

```powershell
$fxBody = "{
  `"fromAccountId`":`"$($alice.id)`",
  `"toAccountId`":`"$($aliceUsd.id)`",
  `"amountMinor`":10000,
  `"reference`":`"Demo KES to USD conversion`",
  `"correlationId`":`"demo-fx-001`"
}"

Invoke-RestMethod -Method Post -Uri "$base/api/v1/fx/convert" -Headers ($auth + @{ 'Idempotency-Key' = 'fx-demo-001'; 'X-Correlation-ID' = 'demo-fx-001' }) -ContentType 'application/json' -Body $fxBody
```

## 9. Verify the Hash Chain

Spoken framing:

> Every entry links to the previous entry hash. This is tamper-evident, not tamper-proof: it helps detect unauthorized mutation of ledger history, but it does not replace access control, backups, or external anchoring.

```powershell
Invoke-RestMethod -Method Get -Uri "$base/api/v1/admin/integrity/verify" -Headers $auth | ConvertTo-Json -Depth 10
```

Expected healthy shape:

```json
{ "isHealthy": true }
```

## 10. Money Printer Stress Test

Spoken framing:

> The real question is whether concurrent requests can create or destroy money. The tests attack the ledger with parallel transfers, bidirectional transfers, concurrent withdrawals, and duplicate idempotency keys.

Run the targeted concurrency tests:

```powershell
dotnet test -c Release --filter FullyQualifiedName~Concurrency
```

Or run the full verified suite:

```powershell
dotnet test -c Release
```

Closing line:

> The real test result for this project is 99 passing tests: 80 unit tests and 19 integration tests, 0 failed, on .NET 10. This is a case study, not a production deployment, but it demonstrates the engineering discipline needed for fintech-grade ledger correctness.
