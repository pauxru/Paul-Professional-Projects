# Portfolio Summary — Real-Time Fraud Detection Event Pipeline

## One paragraph

A production-shaped .NET 10 fraud-detection pipeline built as a **self-directed engineering case
study**. It ingests card and mobile-money transactions over a partitioned in-memory bus, maintains a
ring-buffered per-entity feature store (1 m / 5 m / 1 h / 24 h / 7 d windows), scores every event
against a declarative versioned rule engine (17 rule kinds, including impossible-travel with real
haversine maths, card-testing patterns, and IP-country mismatch), and returns a reproducible 0..1000
risk score with a hard latency budget. Alerts group into cases with a four-eyes approval workflow
for high-exposure write-offs; a shadow / champion-challenger mode lets a candidate ruleset be
evaluated without touching live decisions; and a precision-recall feedback loop measures per-rule
performance against ground-truth-labelled synthetic fraud.

## The signal

- **Ring-buffered aggregator** with tested boundary semantics (edge events, out-of-order arrivals,
  eviction) using a `FakeClock`. Not a `Dictionary<CardId, List<Transaction>>`; a real
  bucket-per-slice ring.
- **Deterministic per-entity ordering** under parallel ingest via an FNV-1a partition hash. Proven
  by a test that fans 100 concurrent producers into a single-reader channel.
- **Reproducible decisions**: same input + same ruleset version = same output. Tested.
- **Latency budget with graceful degradation**: `LatencyBudgetMs = 0` in a test forces the
  degradation path deterministically.
- **Case four-eyes** at exposure ≥ 10,000: approver must not equal proposer. Domain-enforced,
  tested.
- **Shadow-mode ruleset comparison**: `ShadowComparator` computes decision delta by transition
  key (`Approve->Review`, `Review->Decline`, …).
- **69 tests total** (62 unit + 7 integration) with 100 % pass. Integration tests use a shared
  open SQLite `:memory:` connection so migrations run once and are visible to the API.
- **Real measured numbers** in `docs/detection-performance.md` from a seeded synthetic run —
  a full champion-vs-challenger comparison (baseline `v1.0.0`: recall 16.0 % at 100 % precision;
  shipped `v1.1.0`: **recall 55.7 % at 92.2 % precision and 0.45 % FPR**, F1 0.694) together
  with a six-point threshold sweep, per-fraud-pattern catch rates, and per-rule fire counts.
  The comparison — including the harness bugs I found and fixed on the way — is the artefact,
  not any single number.

## What this signals about the engineer

- Comfortable with concurrent programming primitives (`Channel<T>`, per-partition workers,
  lock-per-entity aggregation).
- Comfortable with EF Core gotchas (SQLite can't `ORDER BY DateTimeOffset` — solved with a
  global value converter; `OwnsOne` needs classes, not structs — solved with `sealed record`).
- Understands *why* explainability matters in payments and can defend the trade-off against
  ML-first orthodoxy.
- Writes runbooks before an incident, not after.

## What this does not claim

- Not a product; not deployed anywhere.
- Recall on real traffic is unknown; the 55.7 % number is on a seeded synthetic dataset.
- No PCI-DSS / ISO / SOC 2 audit.
- No cardholder data of any kind ever passed through this code.

## The narrative

The interesting engineering here is not the API surface — it's the **stateful streaming loop**
that has to be right about time. Windows must slide correctly at their edges; late-arriving events
must not silently vanish; the same card must always be routed to the same worker so its aggregates
are consistent; and the whole thing must fit in a **50 ms budget** on a synchronous request path
that can't be papered over with retries.

The interesting *process* here is that the recall number is honest. A less honest report would
show tuned numbers or gloss over the baseline. This one shows the baseline, explains why it is
what it is, and links to the tuning tooling that closes the gap — because the interviewer will
ask "how would you improve this" and the honest answer is more valuable than a fabricated number.
