# Financial Reconciliation & Settlement Engine — Portfolio Summary

**Self-directed engineering case study.** ReconEngine is a .NET 10 financial reconciliation and settlement engine built to model a realistic fintech back office problem: **"the numbers don't match."** In the fictional demo context, Example Bank reconciles merchant settlement data for Contoso Retail against PSP files from PesaGate. Demo currencies include **KES**, USD, and EUR.

## Business problem

Payment operations teams receive internal ledger records and external provider settlement files that disagree because of missing rows, duplicate rows, amount differences, currency mismatches, status differences, date-window issues, fees, and refunds. A credible reconciliation system must ingest messy files, match what can be matched, preserve unmatched exceptions, enforce approval controls, and prove that every run balances.

## What the engine does

- **Streaming ingestion** for CSV and fixed-width files, including row-level rejection reporting and SHA-256 file checksums.
- **Data-driven six-rule matching pipeline**: exact reference, composite reference/amount/date, amount/date window, bounded subset-sum many-to-one and one-to-many, fee-adjusted matching, and refund matching.
- **Exception workflow** with assignment, comments, immutable audit entries, optimistic concurrency, and four-eyes write-off approval.
- **Idempotent immutable runs** with stable exception keys, carry-forward of unresolved records, and no duplicate exception creation on re-runs.
- **Self-checking balance assertion** per currency: internal totals must equal matched plus unmatched internal totals, or the run fails loudly.
- **Synthetic data generator** that emits internal CSV, external CSV, external fixed-width, and a manifest with deterministic defect counts.
- **Performance harness** for measured reconciliation, ingestion, and indexed-vs-naive matching comparisons.

## Technology stack

.NET 10 / C#, EF Core with SQLite by default, minimal APIs, JWT bearer auth, Serilog, OpenTelemetry tracing and metrics, xUnit, WebApplicationFactory integration tests, and console apps for DataGen and PerfHarness.

## Real proof points from this host

- `dotnet build -c Release`: **0 warnings, 0 errors**.
- `dotnet test -c Release`: **118 tests passed** — 105 unit tests and 13 integration tests.
- Reconciliation hot path measured at **230k+ rows/second** on 200k-row and 500k-row runs.
- Exact-reference matching optimization measured **4.7× faster**: naive O(n·m) 153.2 ms vs indexed O(n) 32.5 ms over 40k rows.

## Honest scope

This is a portfolio-grade engineering case study, not a production deployment for a real client. Example Bank, Contoso Retail, and PesaGate are fictional; there are no claimed real users, revenue, uptime, certifications, or production volumes.
