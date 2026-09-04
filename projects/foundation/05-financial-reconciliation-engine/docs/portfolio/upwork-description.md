# Upwork Service Description — Financial Reconciliation Automation

## Stop losing time to settlement mismatches

If your team receives payment, bank, merchant, or PSP files and spends hours asking why **the numbers don't match**, I can build a reconciliation workflow that imports your files, matches records, flags exceptions, and gives finance and operations teams an auditable path to resolution.

## What I build

I build custom reconciliation and settlement back-office systems for fintech-style workflows, including:

- CSV and fixed-width file ingestion.
- Configurable matching rules for references, amounts, dates, fees, refunds, and grouped payments.
- Exception queues for missing records, duplicates, amount mismatches, currency mismatches, fee variances, and date issues.
- Four-eyes approval for sensitive actions such as write-offs.
- Immutable audit trails for comments, assignments, resolutions, approvals, rejections, and reopenings.
- Run reports, aging reports, fee reports, value-by-currency reports, and balance self-checks.
- Local-first prototypes using SQLite, or production-ready integrations based on your environment.

## Typical deliverables

- Requirements mapping for your reconciliation process.
- File format profiles for your internal and external data sources.
- A reconciliation API with authentication and role/scoped authorization.
- Matching rulesets that can evolve without rewriting the core engine.
- Exception workflow with audit history and maker-checker approval.
- Reports and exports in JSON and CSV.
- Tests, performance checks, and documentation for handover.

## Technology

.NET 10, C#, EF Core, SQLite or another relational database as needed, JWT authentication, OpenTelemetry, Serilog, xUnit, and clean modular architecture.

## Why it is trustworthy for financial data

A reconciliation system should not be a black box. The systems I design emphasize:

- **Auditability:** every exception transition and comment is append-only.
- **Four-eyes control:** high-value write-offs require a different approver than the proposer.
- **Idempotency:** re-runs preserve existing exception triage and avoid duplicate exception creation.
- **Balance self-checks:** every run asserts that internal totals reconcile to matched plus unmatched totals per currency.
- **Minor-units money handling:** currency amounts are stored as integer minor units to avoid floating-point drift.

## Case study

As a self-directed engineering case study, I built **ReconEngine**, a .NET 10 Financial Reconciliation & Settlement Engine for a fictional Example Bank / Contoso Retail / PesaGate scenario. It includes streaming CSV and fixed-width ingestion, a data-driven matching pipeline, exception workflow with four-eyes approval, immutable reconciliation runs, synthetic data generation, and a performance harness. On the verified local run, the project built with 0 warnings and 0 errors, passed **118 tests**, reconciled at **230k+ rows/second** on larger benchmark sizes, and showed a **4.7×** indexed matching speed-up over a naive exact-reference comparison.

No real clients, production users, revenue, uptime, or certifications are claimed for this case study.
