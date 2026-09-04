# Upwork Portfolio Description

## Short version (for the profile header)

**Real-time fraud detection pipeline in .NET 10.** Partitioned in-memory bus, ring-buffered
windowed feature store, declarative versioned rule engine with 17 rule kinds, reproducible 0..1000
risk scoring with hard latency budget, four-eyes case management, shadow / champion-challenger
mode, and a precision-recall feedback loop measured against ground-truth-labelled synthetic fraud.
End-to-end champion/challenger tuning cycle in the repo — shipped `v1.1.0` catches **55.7 %** of
injected fraud at **92 % precision** and **0.2 ms p99 scoring latency**, all in 69 tests. Zero
external infrastructure. Self-directed engineering case study.

## Long version (for the project page)

I built a low-latency, stateful, explainable payment-fraud pipeline as a self-directed engineering
case study. The fictional context is **PesaGate Payments** — a card and mobile-money processor
operating in Kenya (KES) and the US (USD). No client work, no real cardholder data, no deployed
users — every transaction in the repository is synthetic and labelled.

**The engineering interesting bits:**

- **Stateful streaming over `System.Threading.Channels`**, partitioned by FNV-1a hash of `CardId`
  so per-entity ordering is preserved under parallel ingest. Proven by a test that fans 100
  concurrent producers into a single-reader channel and asserts strictly-ascending sequence.
- **A ring-buffered per-entity feature store** with 60-second slices and 10,080 buckets giving
  7-day retention. O(1) amortised updates, O(bucket-count) aggregation. Boundary semantics are
  unit-tested with a `FakeClock` — edge events, out-of-order arrivals, and eviction on clock advance.
- **A declarative, versioned, hot-reloadable rule engine** with 17 rule kinds — velocity, unusual
  amount (z-score), impossible-travel (real haversine maths), device sharing, IP reputation, MCC
  risk, card-testing pattern, round-amount, allow / deny lists, and more. Reproducible: same input
  + same ruleset version = same output. Tested.
- **A hard latency budget** with graceful degradation. When exceeded, the decision escalates to
  a conservative default (`Review`) and the reason string records `budget_exceeded`.
- **Four-eyes case management**: alerts group into cases by entity linkage; a case with exposure
  ≥ 10,000 requires a *different* analyst to approve a `ConfirmedFraud` disposition.
- **Shadow / champion-challenger mode**: a candidate ruleset scores every transaction alongside
  the live ruleset without touching the returned decision. `ShadowComparator` produces the
  decision-delta by transition key.
- **Real precision-recall numbers, tuned end-to-end** in the repository. The shipped ruleset
  `v1.1.0` — derived from the tuning recommender + a 6-point threshold sweep against the
  labelled dataset — achieves **precision 92.2 %, recall 55.7 %, FPR 0.45 %, F1 0.694, p99
  scoring latency 0.20 ms**. The conservative baseline `v1.0.0` (precision 100 %, recall 16 %)
  is preserved in the repo and in `docs/detection-performance.md` so the champion-vs-challenger
  delta is visible — that side-by-side comparison, including the harness bugs I found and fixed
  in the process, is the artefact I want reviewers to read.

**The stack:** .NET 10 (C# 13), ASP.NET Core Minimal APIs on port 5015, EF Core + SQLite (adapter-
swappable), JWT bearer auth with four scoped policies, OpenTelemetry metrics + tracing (Console
exporter locally). No Docker required — the whole thing runs from `dotnet run`.

**The tests:** 62 unit tests + 7 integration tests. The integration tests use
`WebApplicationFactory<Program>` with a shared open SQLite `:memory:` connection so migrations
survive between requests. The throughput test scores 5,000 real HTTP transactions through the
full pipeline and asserts a hard wall-clock ceiling. A dedicated `DetectionBenchmarkRunTests`
test executes the whole champion-vs-challenger + threshold-sweep pipeline on the seeded
dataset and asserts the shipped operating point (recall ≥ 55 %, FPR ≤ 3 %) as a piece of
executable acceptance criteria — the reported numbers cannot silently drift.

**What this signals about me:** comfortable with concurrent programming primitives; comfortable
with EF Core gotchas (SQLite `DateTimeOffset` ordering; `OwnsOne` requiring classes); understand
*why* explainability matters in payments; write runbooks before incidents, not after.

**What this project does not claim:** not a product, not deployed anywhere, not audited against
any framework, no real payment traffic. Numbers are on synthetic data.
