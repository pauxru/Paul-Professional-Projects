# Screenshots and Recordings Needed

Capture these assets for the Example Bank — Digital Banking Ledger portfolio page. Use fictional demo data only.

## Must Have

### 1. Project Overview / README Architecture Diagram

**Caption:** Clean modular monolith for a correctness-first banking ledger.  
**Should show:** The README architecture diagram with the four layers: Domain, Application, Infrastructure, and Api.

### 2. API Documentation

**Caption:** ASP.NET Core Minimal API surface for ledger operations.  
**Should show:** OpenAPI at `http://localhost:5013/openapi/v1.json` or the configured API UI if present. Include endpoints for accounts, entries, transfers, holds, reversals, FX, statements, trial balance, and integrity verification.

### 3. Passing Release Test Run

**Caption:** 99 passing tests on .NET 10.  
**Should show:** Terminal output from `dotnet test -c Release` with 99 passed, 0 failed: 80 unit tests and 19 integration tests.

### 4. Concurrency Tests Passing

**Caption:** Stress tests proving concurrent requests do not create money.  
**Should show:** A filtered test run for the four concurrency scenarios: 150 same-account-pair parallel transfers, 150 bidirectional transfers without deadlock, 50 concurrent withdrawals without overdraft, and 50 duplicate idempotency-key requests producing exactly one posting.

### 5. Trial Balance JSON Response

**Caption:** Global trial balance remains balanced.  
**Should show:** `GET /api/v1/reports/trial-balance` response where the ledger totals sum to zero globally.

### 6. Integrity Verification Response

**Caption:** Tamper-evident SHA-256 hash chain verification.  
**Should show:** `GET /api/v1/admin/integrity/verify` returning a healthy response such as `{ "isHealthy": true }`.

### 7. Account Balance Derived from Postings

**Caption:** Balances are explainable from immutable postings.  
**Should show:** A balance response for a fictional Example Bank customer account after funding and transfer activity.

### 8. Statement Export

**Caption:** Statement export with opening balance, movements, closing balance, and running balance.  
**Should show:** `GET /api/v1/statements/{accountId}?from=&to=&format=csv` returning CSV data.

## Nice to Have

### 9. Automated Demo Script Running

**Caption:** End-to-end demo automated through `scripts\demo.ps1`.  
**Should show:** Terminal running the scripted flow: token, accounts, funding, transfer, hold, reversal, FX, trial balance, and integrity check.

### 10. OpenTelemetry Console Metrics

**Caption:** Ledger operations instrumented with OpenTelemetry.  
**Should show:** Console output containing meter `ExampleBank.Ledger` and metrics such as `ledger.posting.latency`, `ledger.entries.posted`, `ledger.imbalance.attempts`, and `ledger.holds.expired`.

### 11. ProblemDetails Validation Error

**Caption:** Invalid ledger requests are rejected with structured errors.  
**Should show:** A deliberately unbalanced journal entry rejected with a ProblemDetails response.

### 12. Idempotency Demonstration

**Caption:** Retried posting requests do not double-post.  
**Should show:** Two requests with the same `Idempotency-Key` returning/replaying one logical result, with only one posting created.

### 13. Hold Capture Flow

**Caption:** Authorization hold with partial capture.  
**Should show:** A hold being placed, partially captured, and reflected in available balance behavior.

### 14. FX Conversion Flow

**Caption:** FX conversion balanced through clearing and rounding gain/loss accounts.  
**Should show:** An FX conversion response and related trial-balance output.

### 15. Security Scope Denial

**Caption:** Scope-based authorization protects ledger operations.  
**Should show:** A token lacking `ledger:post` or `ledger:adjust` being denied for a protected operation.

### 16. Code Structure

**Caption:** Domain invariants isolated from infrastructure.  
**Should show:** Solution explorer or terminal tree with `Domain`, `Application`, `Infrastructure`, `Api`, `UnitTests`, and `IntegrationTests` projects.
