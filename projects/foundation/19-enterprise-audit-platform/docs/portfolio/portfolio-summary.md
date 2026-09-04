# Portfolio summary

**Project 19 — Enterprise Audit & Compliance Event Store**
_A self-directed engineering case study — Owen Rukwaro_

## Elevator pitch

A .NET 10 audit platform that produces a cryptographically verifiable log of security- and
compliance-relevant events. It combines a per-tenant SHA-256 hash chain with periodic RSA-signed
Merkle checkpoints, a canonical JSON serialisation that any language can reproduce, and
retention/legal-hold semantics that preserve chain verifiability even after payload pruning.

Built for the fictional tenant **Example Bank**. **No certification is claimed and no audit
has been performed.**

## What's interesting technically

1. **Chain-by-content-hash design.** The chain link is a function of `contentHash` (and the
   previous chain hash), not the raw payload bytes. This is what makes retention pruning
   coexist with immutability — see ADR-004.
2. **Canonical JSON in ~60 lines.** Deterministic, byte-reproducible, testable. Every
   integrity assertion in the codebase reduces to this serialiser being correct.
3. **Three defence-in-depth layers of append-only enforcement.** Private setters + EF Core
   `SaveChangesInterceptor` + documented DB-level revocation (ADR-005).
4. **Merkle inclusion proofs.** Any single event carries a `(root, path, leafHash, signature)`
   package that a verifier written in any language can validate against the platform's public
   key — no round-trip through the platform required.
5. **Meta-audit with loop prevention.** Every read of the audit log is itself audited, but a
   `ReaderContext.IsMetaAuditor` flag breaks the infinite regress.
6. **A test suite that proves what matters.** 35 unit tests + 27 integration tests including
   tamper detection at four failure modes (payload, delete, reorder, forged inclusion proof),
   a 20 000-event throughput test, and interceptor guarantees.

## Tech stack

- .NET 10, ASP.NET Core Minimal APIs, Entity Framework Core, SQLite (default; drop-in
  Postgres-ready), OpenTelemetry, JWT bearer, xUnit + `WebApplicationFactory<Program>`.
- No Docker, no external Postgres/Redis/Kafka. Runs entirely on a Windows dev host.

## Where to look

- `README.md` — the 21-section walkthrough with Mermaid diagrams.
- `src/AuditPlatform.Domain/Integrity/` — the crypto primitives.
- `src/AuditPlatform.Infrastructure/Persistence/Interceptors/AppendOnlyInterceptor.cs` — the
  runtime append-only enforcer.
- `docs/integrity-model.md` — the cryptographic construction, portable to any verifier.
- `docs/decisions/` — five ADRs walking through the material choices.
- `scripts/demo.ps1` — the 60-second end-to-end demonstration.

## Scale of the work

- ~4 500 lines of production C# across four projects.
- ~2 300 lines of tests across two test projects, all deterministic.
- Zero warnings on `dotnet build -c Release`.
- All 62 tests green on `dotnet test -c Release`.
