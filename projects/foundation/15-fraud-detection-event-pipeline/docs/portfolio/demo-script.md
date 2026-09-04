# Demo Script

Follow this to walk an interviewer through the project in about 8 minutes.

## Setup (before the call)

```powershell
cd 15-fraud-detection-event-pipeline
dotnet build -c Release
dotnet run --project src/FraudPipeline.Api
# leave the API running on http://localhost:5015
```

Open a second terminal for `curl` / `Invoke-RestMethod` calls.

## 1. "Show me one scored transaction"

```powershell
$body = @{ Subject="analyst-01"; Scopes=@("risk:score","risk:investigate","risk:admin","risk:approve") } | ConvertTo-Json
$tok = (Invoke-RestMethod -Uri http://localhost:5015/api/v1/auth/token -Method POST -Body $body -ContentType "application/json").accessToken
$hdr = @{ Authorization = "Bearer $tok" }

$txn = @{
  TransactionRef = "TX-DEMO-001"; CardId = "CARD00001"; CustomerId = "CUST00001"
  DeviceId = "DEV00042"; IpAddress = "203.0.113.10"; MerchantId = "MERCH0007"
  Mcc = "6051"; Amount = 12500; Currency = "USD"; Type = "CardNotPresent"
  Latitude = 40.7128; Longitude = -74.006; Country = "US"
} | ConvertTo-Json
$r = Invoke-RestMethod -Uri http://localhost:5015/api/v1/transactions/score -Method POST -Body $txn -ContentType application/json -Headers $hdr
$r | ConvertTo-Json -Depth 6
```

**Say:** "Every decision is a `ScoreResponse` — score, decision, ruleset version, and the *reasons*.
Look: `mcc-risk: 6051 category weighted high-risk; new-device DEV00042 for CUST00001`. If a
chargeback disputes this decline, we have a defensible explanation."

## 2. "Show me reproducibility"

```powershell
# Change the txn ref, keep everything else identical:
$txn2 = ($txn | ConvertFrom-Json)
$txn2.TransactionRef = "TX-DEMO-002"
$r2 = Invoke-RestMethod -Uri http://localhost:5015/api/v1/transactions/score -Method POST -Body ($txn2 | ConvertTo-Json) -ContentType application/json -Headers $hdr
"first  score=$($r.score) decision=$($r.decision) ruleset=$($r.rulesetVersion)"
"second score=$($r2.score) decision=$($r2.decision) ruleset=$($r2.rulesetVersion)"
```

**Say:** "Same input, same score, same decision. There's a test that pins this invariant."

## 3. "Show me the latency budget"

Point at `appsettings.json`:
```json
"Scoring": { "LatencyBudgetMs": 50, "DegradedDecision": "Review" }
```
**Say:** "There's a unit test that sets this to 0 to force the degradation path. On breach,
decision escalates to `Review` and `budget_exceeded` is persisted with the decision so the analyst
can see it in the case timeline."

## 4. "Show me the feature store"

```powershell
Invoke-RestMethod -Uri http://localhost:5015/api/v1/features/Card/CARD00001 -Headers $hdr
```

**Say:** "That aggregate is O(1)-amortised update, O(bucket-count) aggregate. 60-second slices, 7-day
retention. There's a test that proves boundary correctness at the edge of a window with a `FakeClock`."

## 5. "Show me a shadow ruleset comparison"

The default seed ships an active `v1.0.0` and a shadow `v1.1.0-challenger`. Score a batch of
transactions; then:

```powershell
Invoke-RestMethod -Uri http://localhost:5015/api/v1/rulesets -Headers $hdr
```

**Say:** "The shadow ruleset scores every transaction alongside the live one but never affects
the returned decision. `ShadowComparator` computes the decision delta by transition key. When
we're confident the challenger is better on labelled data, we activate it."

## 6. "Show me the case workflow"

```powershell
# score a burst of risky transactions from the same card
1..8 | ForEach-Object {
  $t = ($txn | ConvertFrom-Json)
  $t.TransactionRef = "TX-CASE-$_"
  Invoke-RestMethod -Uri http://localhost:5015/api/v1/transactions/score -Method POST -Body ($t | ConvertTo-Json) -ContentType application/json -Headers $hdr | Out-Null
}
$cases = Invoke-RestMethod -Uri "http://localhost:5015/api/v1/cases" -Headers $hdr
$cases.items[0]
```

**Say:** "Alerts group into cases by entity linkage. Cases have a state machine — new → assigned →
under-investigation → disposed. If exposure is ≥ 10,000, `ConfirmedFraud` needs a second analyst
to approve. That check is in the domain, not the endpoint, and it's tested."

## 7. "Show me the numbers"

```powershell
Invoke-RestMethod -Uri http://localhost:5015/api/v1/metrics/detection -Headers $hdr
Invoke-RestMethod -Uri http://localhost:5015/api/v1/metrics/latency -Headers $hdr
```

Open `docs/detection-performance.md`. **Say:** "Real measured numbers on the seeded synthetic
dataset — champion vs challenger, side by side. The baseline `v1.0.0` is 100 % precision, 16 %
recall. The shipped `v1.1.0` catches 55.7 % of injected fraud at 92 % precision and 0.45 % FPR,
F1 0.694, at 0.2 ms p99 scoring latency. That side-by-side — plus the threshold sweep and the
per-fraud-pattern breakdown underneath it — is the artefact. It's what a risk manager would
review before promoting a ruleset in production."

## 8. Anticipate the questions

- **"Why not Redis / Flink?"** → [ADR-0001](../decisions/0001-in-memory-feature-store.md).
- **"How do you keep decisions reproducible?"** → [ADR-0002](../decisions/0002-declarative-versioned-rules.md).
- **"How do you preserve ordering?"** → [ADR-0003](../decisions/0003-partitioning-strategy.md).
- **"What happens on slow days?"** → [ADR-0004](../decisions/0004-latency-budget-degradation.md).
- **"Why not ML?"** → [ADR-0005](../decisions/0005-explainability-first.md).
- **"What would you do next?"** → Redis adapter, threshold auto-tuner, signed rulesets, ML challenger.
