# Runbook — Scoring Latency Breach

## Signal

- The metric `fraud_pipeline.scoring.duration_ms` histogram p99 crosses the configured
  `Scoring:LatencyBudgetMs` (default 50 ms).
- The counter `fraud_pipeline.decisions_total{reason="budget_exceeded"}` starts climbing above
  its ambient rate (which should be near zero).
- Alerts have `reasons` strings containing `budget_exceeded` in the persisted decision log.

## Confirm

1. `GET /api/v1/metrics/latency` and read p50 / p95 / p99.
2. Grep the decision log for the last N decisions that carried a `budget_exceeded` reason. In a
   real deployment this would be a log query; in the current build query the DB directly:
   ```sql
   SELECT TransactionRef, DecidedAt, LatencyMs, Reasons
   FROM ScoringDecisions
   WHERE Reasons LIKE '%budget_exceeded%'
   ORDER BY DecidedAt DESC
   LIMIT 20;
   ```
3. If the count is a handful of one-offs and p99 is still under budget, it is noise. Investigate
   only if it is sustained (say, >1 % of decisions over a 5-minute window).

## The four common causes

1. **A newly activated ruleset with an expensive rule** — most common cause. The rule engine is
   O(rules × recent-history-length); if you added a rule that scans a longer horizon or uses a
   nested loop over the history slice, it will show up here. Confirm by rolling back the ruleset
   (see `ruleset-rollback.md`) and watching the metric.
2. **Feature-store cold start** — after a restart, per-entity aggregators for cold entities are
   empty and get built lazily on first access. This causes a brief spike, not a sustained breach.
3. **SQLite lock contention** — if the process is being asked to serve very high write throughput
   at the same time as scoring reads. Mitigation: increase the busy timeout in the connection
   string, or use `PRAGMA journal_mode=WAL` (already set by default in the code path).
4. **A misbehaving rule with a runaway loop** — should have been caught in code review. Check the
   ruleset JSON for any typo that could turn a bounded lookup into an unbounded one.

## Mitigate

1. **Do not disable rules under load** — that changes decisions retroactively for downstream
   analysts. Use the ruleset activation flow.
2. **Graceful degradation is already on** — decisions past the budget become Review. That is by
   design; it protects the merchant experience while the engineering fix lands.
3. If p99 crosses 5× budget: **evict feature-store state for entities with implausibly large
   histories**. Look for entities with `SnapshotCount > 100k` — those are likely bad data. This
   is a manual db operation today (`DELETE FROM CardStates WHERE ...`); a runtime endpoint for it
   is on the roadmap.
4. If the breach follows a deployment, roll back the deployment.

## Verify the fix

- Watch p99 for 10 minutes after the mitigation. The metric must return to under budget without
  further intervention.
- Confirm `decisions_total{reason="budget_exceeded"}` has stopped incrementing.
- Reprocess the affected transactions if the decision was `Review` when it should have been
  `Approve` — but only after an analyst has confirmed on a sample basis that the auto-review was
  spurious.

## After the fix

- Add a benchmark test that exercises the expensive path. The existing throughput test
  (`ScoringApiTests.Throughput_ScoresMoreThan5000TransactionsInBoundedTime`) is a wall-clock
  smoke check; a rule-specific micro-benchmark would give you an early-warning signal in CI.
- Consider whether the `LatencyBudgetMs` value is right. 50 ms is the default; some deployments
  need 25 ms or 100 ms depending on the upstream contract with the payment gateway.
