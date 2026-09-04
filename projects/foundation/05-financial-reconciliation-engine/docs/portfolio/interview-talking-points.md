# Interview Talking Points — Financial Reconciliation & Settlement Engine

## Q: What problem were you solving?

A: I built a self-directed .NET 10 case study for the fintech problem where internal ledger records and external settlement files disagree. The system imports files, normalizes records, applies matching rules, classifies exceptions, supports audited resolution, and asserts that every run balances.

## Q: Why make matching rules data-driven?

A: Reconciliation logic changes as providers, fees, formats, and tolerances change. A data-driven `MatchingRuleSetDefinition` lets the engine snapshot the active ruleset version on each run and preserve the audit trail. Editing a ruleset creates a new version instead of mutating history, which is important when explaining why a record matched at a specific point in time.

## Q: Walk me through the matching pipeline.

A: The verified ruleset uses a priority-ordered pipeline: exact reference, composite reference/amount/date, amount/date window, bounded many-to-one and one-to-many subset-sum, fee-adjusted matching, and refund matching. Each match records the rule ID, ruleset version tag, confidence, match kind, and explanation.

## Q: How do idempotency and carry-forward work?

A: Runs load records whose `ReconStatus` is not `Matched`. Matched records leave the future working set. Unmatched records carry forward and are re-evaluated. Exceptions are upserted by a stable content-derived `ExceptionKey`, so re-running the same inputs does not create duplicate exceptions and preserves existing triage.

The known trade-off is that a re-run's **in-scope** exception count can be less than or equal to the first run, because already matched records are excluded. The **total open exception set** stays stable. This was verified with a seed where the first run produced 250 exceptions and total open exceptions stayed 250 across re-runs.

## Q: Why store money in minor units and use banker's rounding?

A: Financial calculations should avoid binary floating-point drift. The domain represents money as decimal input plus ISO currency, then stores minor units as `long`. Currency decimals are explicit: KES, USD, EUR, and GBP use 2; JPY uses 0; BHD uses 3; unknown currencies default to 2. Midpoint rounding uses `MidpointRounding.ToEven`, which is banker's rounding.

## Q: How do you handle KES examples?

A: KES uses 2 decimal places, so KES 1,000.00 is stored as 100,000 minor units. The configured write-off approval threshold is 100,000 minor units, so a KES write-off at or above 1,000.00 requires four-eyes approval.

## Q: What is the bounded subset-sum trade-off?

A: Many-to-one and one-to-many matching can become combinatorially expensive. The engine caps group size at 4, candidate count at 20, and date window at 3 days. That bounds worst-case work instead of allowing unbounded O(n²) or worse behavior. The trade-off is explicit: very large grouped settlements may be missed in exchange for predictable runtime.

## Q: Why streaming ingestion instead of reading files into memory?

A: The ingestion layer uses `IAsyncEnumerable` tokenizers for CSV and fixed-width data so large files do not need to be loaded fully into memory. The verified facts include a 250k-row generation path and a 100k-row streaming CSV parse/normalize benchmark. Streaming also supports row-level rejection handling without failing the whole file.

## Q: How does the four-eyes workflow work?

A: Exceptions follow a state machine: Open → Assigned → Resolved, or Open/Assigned → PendingApproval → Resolved for approval-required write-offs. Resolved exceptions can be reopened. A write-off with absolute amount at or above 100,000 minor units requires approval, and the approver must be a different user from the proposer. Illegal transitions throw domain exceptions.

## Q: How is the audit trail protected?

A: Every transition and comment appends an immutable `ExceptionAuditEntry` with actor, action, from/to status, detail, and timestamp. The exception also has a version field for optimistic concurrency. Enums are persisted as strings, making audit rows readable.

## Q: How did you test defect counts?

A: The synthetic generator emits a `manifest.json` with ground-truth defect counts. The `all` profile includes deterministic counts: DuplicateInternal 50, DuplicateExternal 30, AmountMismatch 40, MissingInExternal 30, MissingInInternal 20, CurrencyMismatch 20, StatusMismatch 20, DateOutOfWindow 15, FeeVariance 25, plus clean fee-adjusted and refund cases. Expected exceptions are 250. Tests compare engine behavior against that manifest so matching defects do not silently cross-contaminate.

## Q: What observability choices did you make?

A: The API uses Serilog request logging, correlation-id middleware, OpenTelemetry tracing, and metrics. Traces include spans for `recon.run`, `recon.load`, `recon.calculate`, and `recon.persist`. Metrics include rows per second, match rate, and open-exceptions gauge. This makes reconciliation runs explainable in logs, traces, and operational metrics.

## Q: What real performance numbers can you defend?

A: On the verified host, the perf harness measured reconciliation hot path throughput above **230k rows/second** on 200k-row and 500k-row cases. It also measured an exact-reference optimization over 40k rows: naive O(n·m) took 153.2 ms and indexed O(n) took 32.5 ms, a **4.7× speed-up**. These are local benchmark numbers, not production claims.

## Q: What are the main limitations or honesty constraints?

A: This is a self-directed case study using fictional data. There are no real clients, users, revenue, uptime, or certifications. Docker artifacts exist but were not verified on the host because Docker was not installed. The default database is SQLite, which is appropriate for the zero-infrastructure case study and local demo, not a claim that SQLite is the right production database for every deployment.
