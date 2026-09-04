# Interview Talking Points

Structured around the ten portfolio questions. The strongest signals are questions **3 and 4** (what
breaks and how it recovers) — lead with those if time is short.

## 1. What real business problem does this solve?

Accounts-payable automation. The value is not reading an invoice (OCR/LLMs do that) but **deciding
which machine-read documents are safe to pay automatically** and routing the rest to a human with the
right evidence. The measurable outcome is the straight-through-processing rate.

## 2. Why is the architecture non-trivial?

It is a guardrail system, not a model demo. The interesting parts are: a spatial extractor that works
on word coordinates; a deterministic validation layer including a **three-way match** with tolerances;
a **weakest-link confidence aggregation** feeding explicit routing thresholds; a human-in-the-loop
review subsystem with claim leases and SLA; and a **correction feedback loop** that learns per-supplier
anchors. All behind a strict layered architecture (Domain ← Application ← Infrastructure ← Api) so
every adapter (classifier, extractor, store, ERP) is swappable.

## 3. What could fail? *(lead with this)*

- A supplier changes their template → extraction confidence drops → documents pile into review.
- The ERP is down or flaky → exports fail.
- An ambiguous ERP response → risk of **double-booking** a payment.
- A degraded/OCR-noisy document → a critical field is mis-read.
- A reviewer claims a task and walks away → work gets stuck.
- A malicious or oversized upload; a CSV with a formula-injection payload; a path-traversal filename.

## 4. How does the system recover? *(lead with this)*

- **Feedback loop:** correcting one document teaches the anchor so the supplier's next documents
  auto-extract — the backlog is self-healing at the source.
- **Idempotent export:** an idempotency key (`{documentId}:v{version}`) means retries never
  double-book; the ERP returns the original reference for a repeated key.
- **Bounded retries + dead-letter:** transient export failures retry to a limit then dead-letter for
  operators; the queue is re-drivable safely once the ERP is healthy.
- **Weakest-link confidence + hard-failure block:** a mis-read critical field or any hard validation
  failure prevents auto-approval and routes to review — the guardrail catches exactly these.
- **Claim leases:** abandoned review claims expire and return to the queue.
- **State machine + reprocess:** illegal states are impossible; failed documents are auditable and can
  be reset to `Received` and re-run.

## 5. How is it secured?

JWT bearer with four **policy-based** permissions; dev-token endpoint only in Development; startup
refuses the default signing key in Production. Untrusted-file handling (size/content-type allow-lists,
hashing, no execution), path-traversal-safe object-store keys (verified inside root),
CSV/formula-injection neutralisation on export, authenticated access to document PII, security headers,
rate limiting. Full STRIDE review with explicit non-claims in `docs/security/`.

## 6. How is it tested?

125 tests (112 unit + 13 integration). Unit tests cover every validation rule (pass/fail/boundary),
three-way match (partial/over delivery, over-billing), spatial table detection, confidence routing at
threshold boundaries, the state-machine matrix, fuzzy matching (with a negative case), and the
feedback loop. Integration tests boot the real API against throwaway SQLite and cover auth (401/403),
upload validation, the happy path, and STP. `IClock`/`FixedClock` everywhere time matters. Accuracy and
STP are measured, not asserted arbitrarily.

## 7. How is it observed in production?

OpenTelemetry metrics (pipeline stage durations, STP rate, review queue depth, processed/auto-approved
counters, extraction-confidence histogram) and tracing; Serilog structured logs with correlation ids
on every request. The three operational failure modes have runbooks.

## 8. What trade-offs were made?

Deterministic-first extraction over an LLM (reproducible/offline vs recall); synthetic `.ocr.json`
layout vs real OCR (dependency-free/deterministic vs realism); weakest-link confidence (safety vs a
higher STP); per-supplier hints vs model retraining (instant/testable vs cross-supplier generalisation);
modular monolith vs microservices (simplicity vs independent scaling). Each is an ADR.

## 9. How would it scale?

The monolith scales horizontally behind a queue; the pipeline stages are stateless given the document.
SQLite → Postgres is a configuration change (provider-agnostic model). Object store → Azure Blob, ERP
→ a real HTTP client, both behind existing ports. Review and export are naturally partitionable.
High-volume deployments would move stage execution onto a durable queue and add idempotent workers.

## 10. What would change in a real enterprise deployment?

OIDC/JWKS instead of a dev signing key with secret-manager rotation; a real OCR/PDF parser adapter;
Azure Blob storage with malware scanning; strict SSRF allow-listing on a real ERP HTTP client;
tenant isolation with EF global query filters; centralised audit and alerting on dead-letter growth and
authz denials; and a reviewer analytics dashboard. All are documented as extension points.

## The one-liner

"I didn't build an invoice reader — I built the guardrails that decide whether a machine-read invoice
is safe to pay, and I measured the straight-through rate honestly. The hard parts are the three-way
match, the weakest-link confidence, the idempotent export, and a feedback loop I can prove with a test."
