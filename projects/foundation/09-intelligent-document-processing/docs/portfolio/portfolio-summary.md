# Portfolio Summary

**Intelligent Document Processing Platform** — a self-directed engineering case study in building the
**guardrails around AI**, not the AI. It automates accounts-payable document handling (invoices,
purchase orders, delivery notes) for the fictional Acme Manufacturing and proves, with a measured KPI,
what fraction of documents it can process straight-through without a human.

## One-paragraph pitch

A C#/.NET 10 document pipeline ingests supplier documents, classifies them with an explainable model,
extracts typed fields with coordinate-aware (spatial) strategies, runs a battery of deterministic
validation rules — arithmetic, dates, duplicates, currency, fuzzy supplier matching, tax-id format and
a full three-way match of invoice ↔ PO ↔ delivery note — then aggregates confidence and **routes**
each document to auto-approve, human review or reject. Approved documents export to a simulated ERP
with retries, an idempotency key and a dead-letter path. Reviewer corrections feed back into
per-supplier extraction hints so the next document extracts better. Everything runs offline with only
the .NET SDK; there are no paid APIs and no network calls in the build or tests.

## What makes it non-trivial

- **Guardrail layer** — deterministic validation + three-way match + weakest-link confidence + explicit
  routing thresholds. This is the part enterprises actually pay for.
- **Measured STP** — the straight-through-processing rate is computed by the system over a
  deterministic corpus (31.58%), not asserted.
- **Proven feedback loop** — a test shows v1 mis-extracts a field, a correction learns the anchor, and
  v2 extracts it correctly.
- **Designed failure modes** — idempotent export, bounded retries + dead-letter, claim leases,
  reprocessing, and an audited state machine.

## Measured results (reproducible)

| Metric | Value |
| --- | --- |
| Classification accuracy | **100.00%** (19/19) |
| Extraction accuracy | **98.78%** (81/82 fields) |
| Straight-through-processing rate | **31.58%** (6/19) |
| Tests | **125 pass** (112 unit + 13 integration) |
| Build | `dotnet build -c Release` clean (0 warnings) |

## Stack

ASP.NET Core minimal APIs (.NET 10), EF Core + SQLite, JWT with permission policies, OpenTelemetry,
Serilog, xUnit + NSubstitute. Optional LLM classifier behind configuration (off by default, stub-tested).

## Honest scope

Self-directed portfolio project — **not** client work. All data is synthetic and fictional. No real
company, supplier, revenue, user, transaction, uptime or certification is represented or claimed. The
synthetic `.ocr.json` layout format stands in for real OCR/PDF; Docker files are included but
unverified (no Docker on the build host).

## Where to look

- `README.md` — full walkthrough with three Mermaid diagrams.
- `docs/decisions/` — five ADRs on the key trade-offs.
- `docs/security/security-review.md` — STRIDE analysis + explicit non-claims.
- `docs/accuracy-report.md` — the measured numbers and how they are produced.
- `docs/test-results.md` — real build and test output.
