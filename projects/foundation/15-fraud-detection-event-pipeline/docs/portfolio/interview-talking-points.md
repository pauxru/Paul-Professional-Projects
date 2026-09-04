# Interview Talking Points

## Opening 30 seconds

> "This is a real-time fraud-detection pipeline in .NET 10, built as a case study. The interesting
> engineering is a stateful streaming loop with hard latency budgets, a ring-buffered feature
> store, a versioned rule engine, and a shadow ruleset mode. I can walk you through any layer, but
> I'll start with the concurrency model because that's the part interviewers usually want to see."

## The concurrency model (2 minutes)

> "Ingestion is a `PartitionedTransactionBus` — an array of bounded `System.Threading.Channels`
> keyed by an FNV-1a hash of the card id. That's how I get per-entity ordering under parallel
> ingest. Same card, same partition, same reader — so I never race two updates to the same
> aggregate. There's a test that fans 100 concurrent producers and asserts strictly-ascending
> sequence out of the reader."

## The feature store (2 minutes)

> "Each entity has a `WindowedFeatureAggregator`. It's a ring buffer of 10,080 buckets, 60 seconds
> each — 7 days of retention. Updates are O(1) amortised; aggregation over a sub-window is O(bucket-count).
> The DB is never queried by the scoring path. The whole thing is rebuildable by replaying the
> transaction log, which is what makes it safe to keep it in-process."

> "The interesting test is boundary correctness. A bucket whose slice-start equals the window start
> is included; a bucket whose slice-start is strictly less is excluded. There's a test with a
> `FakeClock` that pins that semantic."

## The rule engine (2 minutes)

> "17 rule kinds. Velocity, unusual amount via z-score, impossible-travel with real haversine
> maths, device sharing, IP reputation, MCC risk, card-testing pattern — the standard payments
> taxonomy. Each rule returns a contribution, weight, and human-readable reason. Weighted sum
> gives a 0..1000 score; band thresholds classify into Approve/StepUp/Review/Decline."

> "The reason I chose rules over ML is documented in ADR-5. It's not that I can't train a model —
> it's that in payments the decline needs to be defensible in a chargeback dispute. Analyst
> throughput collapses when reason codes are shallow."

## The reproducibility invariant (1 minute)

> "Every decision records the ruleset version in force at decision time. `ScoringService.EvaluateOnly`
> is a pure re-scoring path — no I/O, no state mutation. So replay against historical labels is
> mechanical: pull the transactions, pull the labels, run `EvaluateOnly`, compute confusion matrix.
> That's how the tuning recommender works."

## The latency budget (1 minute)

> "Configurable, defaults to 50 ms. When exceeded, decision escalates to a conservative default —
> Review, unless allow-listed. The persisted reason includes `budget_exceeded` so the analyst
> can distinguish 'we reviewed this because of the rules' from 'we reviewed this because we were
> slow'. Test that pins this uses `LatencyBudgetMs = 0` to guarantee the branch."

## The four-eyes workflow (1 minute)

> "Alerts group into cases by entity linkage. A case with exposure ≥ 10,000 that an analyst wants
> to close as `ConfirmedFraud` goes to `AwaitingApproval` — the approver must not equal the
> proposer. That check is in the domain, not the endpoint, so it survives API refactors. Tested."

## The champion vs challenger numbers (1 minute — critical)

> "There are two rulesets in the repository. `v1.0.0` is the conservative baseline — 100 %
> precision, 16 % recall on the seeded dataset. `v1.1.0` is what actually ships — I ran the
> tuning recommender end-to-end, derived the challenger, ran a threshold sweep, promoted the
> F1-optimal operating point, and reported the honest side-by-side. The shipped ruleset catches
> **55.7 % of injected fraud at 92 % precision and 0.45 % false-positive rate**, F1 0.694, at
> **0.2 ms p99 scoring latency**. The baseline is kept in code and in the docs so the delta
> stays legible — that champion/challenger comparison, including the harness bugs I found and
> fixed on the way, is the artefact I want a reviewer to read."

> "The tuning cycle isn't just 'lower the thresholds'. On the way I root-caused a real bug in
> the feature store where the observe-vs-score ordering was overwriting `LastLocation` before
> scoring, so the impossible-travel rule was comparing every transaction to itself — that fix
> lifted recall from 16 % to about 40 %, before I touched a single band. The threshold sweep
> then turns 'my recall was low' into 'I can navigate the precision/recall operating curve
> and pick a defensible point', which is what a senior payments engineer actually does."

## If they ask "what would you build next"

- Redis adapter behind `FeatureStoreRuntime` for horizontal scale.
- Threshold-parameter auto-tuner (today the recommender only proposes weight changes).
- Signed rulesets (Ed25519) so tampering with the definition JSON is detectable at load.
- An ML challenger in the shadow slot, using the same `FeatureVector` the rules already produce.
- Extract a first-class replay CLI from the existing `Rebuild` code path.

## If they ask "what would you change if you started over"

- I'd invest even earlier in the tuning recommender / threshold sweep — the champion/challenger
  workflow is the most compelling part of the story and it should be the first thing built, not
  the last. I ran the loop for real in the coordinator's review pass; it should have been the
  spine of the project from day one.
- I'd have written a fake `IIpReputationSource` port from day one instead of the inline synthetic
  list — the inline list is fine for now, but it's a shape mismatch with the rest of the
  architecture.
- I might have chosen `NodaTime` instead of `DateTimeOffset` to avoid the SQLite ordering bug.
  (The workaround is a global value converter and it's tested — but `NodaTime` would have been
  cleaner from day one.)

## If they ask "what went wrong"

- The first build failed because I made `Money` and `GeoLocation` `readonly record struct`s, and
  EF `OwnsOne` requires reference types. Converted to `sealed record` classes with private
  parameterless constructors. Real lesson: EF Core has opinions about value objects; know them.
- Second: SQLite silently can't `ORDER BY DateTimeOffset`. Manifested as a 500 in the integration
  test only after the first EF query landed. Fixed with `DateTimeOffsetToBinaryConverter` in a
  global convention. Real lesson: the "we run against SQLite for tests, Postgres for prod" pattern
  hides a class of bugs; test the *actual* provider.
- Third: `DataAnnotations` on request DTOs are **not** auto-validated in Minimal APIs. The
  invalid-MCC test was failing 500 instead of 400 because my domain constructor was throwing
  outside my try/catch. Widened the try/catch. Real lesson: Minimal APIs are minimal.
