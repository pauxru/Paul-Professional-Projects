# Runbook — Alert Flood

## Signal

- The metric `fraud_pipeline.alerts_created_total` shows an order-of-magnitude jump over the
  rolling 5-minute baseline.
- The `/api/v1/alerts` page count balloons past ten pages in a few minutes.
- Analysts report they cannot triage fast enough; SLA aging on new cases climbs.

## Confirm

1. Hit `GET /api/v1/metrics/detection` — read `alerts` and compare to the previous baseline
   snapshot (`docs/detection-performance.snapshot.json`). Rate-per-minute is a much better signal
   than absolute count.
2. Hit `GET /api/v1/rulesets` and check that `IsActive` matches the ruleset version you expect.
   A rogue activation is the number-one cause of an alert flood in this platform.
3. Inspect the top-firing rule via
   `GET /api/v1/metrics/latency` and the `fraud_pipeline.rules_fired_total{rule_id=...}` counter.
   One rule at 10× the rest almost always means a threshold change.

## Mitigate

**In order of least destructive to most destructive.** Prefer a shadow-mode rollback over a hot
ruleset change.

1. **Snapshot state**: `GET /api/v1/rulesets` and store the current active version somewhere
   durable. Every mitigation you do below must be reversible.
2. **Widen the alert filter for triage**: analysts should filter to `score >= (currentThreshold + 100)`
   in the UI to reduce the queue while you diagnose. This does not change the scoring pipeline; it
   only changes what the operators see.
3. **Roll back the ruleset**:
   - Find the previous known-good version: it is the second row (by `ActivatedAt`) in the
     `rulesets` list.
   - `POST /api/v1/rulesets/{prevVersion}/activate` with a bearer token that carries `risk:admin`.
   - Confirm `GET /api/v1/rulesets` now shows the previous version as `IsActive`.
   - This is documented separately in `ruleset-rollback.md`.
4. **Kill switch (last resort)**: set `Scoring:LatencyBudgetMs` to `0` via env var and restart. The
   scoring service will degrade every decision to Review immediately (`budget_exceeded` reason).
   This stops declines but *does not stop alerts* — use it to buy time while you get an approver
   to sign off on the rollback.

## Post-incident

- Write a timeline. Which rule fired unexpectedly? Which analyst signed off on the change? What
  was the alert-per-minute peak?
- Add a synthetic dataset test that reproduces the flood signal, so a future change to the same
  rule would fail CI before it went live.
- If the flood was caused by a threshold change, capture the projected impact number *before*
  activation, using `POST /api/v1/rulesets/simulate` — this is the whole point of shadow mode.
- Review whether the four-eyes activation policy (currently on cases only) should also apply to
  ruleset activation.
