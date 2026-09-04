# ADR-0004 — Latency budget & graceful degradation

**Status:** Accepted
**Date:** 2026-01-15

## Context

The scoring endpoint sits in the merchant's synchronous approval path. A 500 ms outlier is worse
than a slightly weaker decision — a payment terminal that stalls loses the transaction anyway, and
the merchant's confidence in the platform along with it.

At the same time, we cannot simply time out and drop the decision: an unattended `Approve` is much
worse than a defensive `Review`.

## Decision

Enforce a **configurable hard latency budget** (`Scoring:LatencyBudgetMs`, default 50 ms) around
the scoring pipeline. When the budget is exceeded:

- If an **allow-list** rule fired, the decision remains `Approve` (allow-list is precedence).
- Otherwise the decision is **escalated to `Scoring:DegradedDecision`** (default `Review`) unless
  the already-computed decision is already at least that severe (i.e., we never *soften* on breach).
- The persisted `Reasons` string appends `; budget_exceeded`, and a `BudgetExceeded=true` flag is
  persisted on the `ScoringDecision`.

The pipeline **never times the request out**. The budget is a soft ceiling that changes the
decision, not the operational SLA of the endpoint.

## Consequences

**Positive:**
- Conservative failure mode: overloaded system → more reviews, not more fraud.
- Analysts see the `budget_exceeded` breadcrumb in the case timeline and can distinguish "we
  reviewed this because of the rules" from "we reviewed this because we were slow".
- Testable: `ScoringServiceTests.Score_ExceedsLatencyBudget_DegradesToReview` uses
  `LatencyBudgetMs = 0` to force the path.

**Negative:**
- If the underlying store is broken, the whole pipeline degrades to `Review`. Downstream analyst
  capacity must be sized for the worst-case degraded rate. In practice we'd add an emergency
  "read-only skip" mode gated by an operator flag.
- The degradation is a *global* choice; per-tenant / per-merchant budgets would be a natural
  extension.

## Alternatives considered

- **Time out and return 503.** Rejected — the merchant retries, we double-score, and per-entity
  ordering breaks.
- **Time out and default to `Approve`.** Rejected — trivially exploitable.
- **Time out and default to `Decline`.** Rejected — punishes honest customers on a bad day; risk
  officers would push back.

## Related

- `src/FraudPipeline.Application/Scoring/ScoringService.cs` (see `ScoreWithRulesetAsync` and
  `Aggregate`).
- `runbooks/scoring-latency-breach.md`.
- Tests: `ScoringServiceTests.Score_ExceedsLatencyBudget_DegradesToReview`.
