# Upwork Portfolio Description

## Short version (profile / proposal snippet)

```
Intelligent Document Processing Platform — self-directed engineering case study

Problem: Accounts-payable teams must decide which machine-read supplier documents are safe to pay
automatically and route the rest to humans with the right evidence.
Built: A C#/.NET 10 document pipeline — ingestion, explainable classification, spatial extraction,
deterministic validation incl. three-way match, confidence routing, human review, correction
feedback, and idempotent ERP export.
Engineering focus: guardrails around AI, weakest-link confidence aggregation, a measured
straight-through-processing rate, a per-supplier learning feedback loop, idempotent export with
dead-letter, an audited pipeline state machine.
Stack: ASP.NET Core, EF Core/SQLite, JWT, OpenTelemetry, xUnit.
Verification: 125 tests pass; classification 100%, extraction 98.78%, STP 31.58% measured over a
deterministic seeded corpus; zero network calls in build/test.

This is a self-directed portfolio project, not client work.
```

## Long version (portfolio page)

**Intelligent Document Processing Platform** is a self-directed engineering case study in the part of
document AI that enterprises actually pay for: **the guardrails**, not the model.

Accounts-payable teams receive invoices, purchase orders and delivery notes in inconsistent layouts.
Reading them is the easy part; deciding which are safe to pay automatically — and catching the ones
that are not before money moves — is the hard part. This platform implements that decision as an
explicit pipeline: ingest → classify → extract → validate → score confidence → route to auto-approve,
human review or reject → export to the ERP.

The engineering emphasis is on trust and failure handling:

- **A deterministic guardrail layer** — arithmetic, date sanity, duplicate detection, currency
  consistency, fuzzy supplier matching, tax-id format validation, and a full **three-way match** of
  invoice ↔ purchase order ↔ delivery note with realistic tolerances for partial deliveries and price
  drift.
- **Weakest-link confidence aggregation** so one badly-read critical field cannot be masked by many
  good ones — auto-approval is conservative by design.
- **A measured KPI** — the system computes its own **straight-through-processing rate** over a
  deterministic corpus (31.58%), reported honestly rather than asserted.
- **A correction feedback loop** — reviewer corrections learn per-supplier extraction anchors so the
  next document from that supplier extracts better; proven end-to-end by a test.
- **Designed failure modes** — idempotent ERP export (a retry never double-books), bounded retries
  with a dead-letter queue, review claim leases, document reprocessing, and an audited state machine.

Built with ASP.NET Core (.NET 10), EF Core/SQLite, JWT with permission-based authorization,
OpenTelemetry and Serilog. It runs and tests entirely offline with only the .NET SDK — **125 tests
pass**, classification is **100%** and extraction **98.78%** over the seeded corpus, with **zero
network calls** in the build or tests.

*Self-directed portfolio project. All data is synthetic and fictional; no client, revenue, users,
uptime or certifications are represented or claimed.*

## Suggested tags / skills

C#, .NET, ASP.NET Core, Entity Framework Core, SQLite, REST API design, JWT/authorization,
OpenTelemetry, document processing, AI guardrails, domain-driven design, xUnit, clean architecture.
