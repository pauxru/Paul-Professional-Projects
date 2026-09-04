# Client-facing description (Upwork / freelance profile)

**Enterprise Audit & Compliance Event Store — self-directed engineering case study.**

I built a .NET 10 audit platform from first principles: append-only event ingest, a
per-tenant SHA-256 hash chain, periodic RSA-signed Merkle checkpoints with single-event
inclusion proofs, canonical JSON serialisation any language can reproduce, and retention with
tombstoning that preserves chain verifiability across pruned data.

The platform includes JWT-scoped access control, streaming NDJSON/CSV exports, signed evidence
packs, a privileged-access anomaly report, a schema registry with backwards-compatibility
validation, a dead-letter store, keyset-paginated queries, and full-text search on a hand-rolled
inverted index. Everything runs on a single Windows dev host — SQLite by default, no Docker,
no external Postgres/Redis. `dotnet build` and `dotnet test` both pass with zero warnings
and 62 tests green.

Documented deliverables include a 21-section README with Mermaid diagrams, five ADRs on the
major design decisions, a security review, control-theme mapping notes, a database schema
document, an integrity model reference, three operational runbooks, portfolio-facing summaries,
and an end-to-end demo script that ingests events, verifies the chain, tampers with the DB
directly, catches the tamper at the exact sequence, and builds a signed evidence pack an
auditor could round-trip.

**Important framing.** This is a fictional platform on a fictional tenant ("Example Bank").
I make **no compliance claims** — SOC 2, PCI DSS, HIPAA, ISO 27001, GDPR "compliance", or any
other regulatory certification. The compliance-notes document is explicit about this. What
this project *does* demonstrate is engineering competence with:

- Cryptographic protocol design and correct implementation.
- Clean Architecture in .NET across Domain / Application / Infrastructure / Api.
- EF Core interceptor engineering for invariant enforcement.
- Thorough testing including tamper-detection scenarios and 20 000-event throughput proofs.
- API design (Minimal APIs, ProblemDetails, JWT scopes, rate limiting, OpenTelemetry,
  correlation ids, health probes).
- Comprehensive technical writing including ADRs, runbooks, and a portable integrity spec.

If you have a similar auditability, compliance-adjacent, or event-sourcing problem and want
to see the level of care I bring to systems-level engineering work, this is the project I
would show you first.
