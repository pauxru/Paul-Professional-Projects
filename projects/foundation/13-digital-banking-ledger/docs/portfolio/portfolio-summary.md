# Example Bank — Digital Banking Ledger

## Portfolio Classification

Self-directed engineering case study. This is a reference implementation / production-style prototype, not client work and not a claim of live production use.

## Elevator Pitch

Example Bank — Digital Banking Ledger is a .NET 10 core-banking ledger engine built around one rule: it must be impossible to create money. It models accounts, journal entries, transfers, holds, reversals, fees, interest, FX conversion, statements, reconciliation, and integrity verification while enforcing double-entry accounting invariants under concurrent writes.

The project demonstrates how a backend system can treat financial correctness as the primary product feature: every posting is append-only, every entry must balance per currency, balances are derivable from postings, and concurrency behavior is tested against failure modes that would otherwise create inconsistent money movement.

## Business Problem

In a banking or payments backend, a balance column is not enough. If two transfers race, a reversal is applied twice, a fee is rounded incorrectly, or an entry is partially written, the system can silently create or destroy money.

A ledger should make invalid financial states unrepresentable. Corrections should be recorded as new entries, not destructive edits. The system should be able to explain every balance from the immutable posting history and prove that the total ledger still balances globally.

## Standout Engineering

- **Concurrency correctness proof:** per-account in-process locks are acquired in ascending account-id order, followed by a global hash-chain lock, with optimistic-concurrency `Version` columns, serialized transactions, bounded retry, and idempotent replay.
- **Double-entry invariants:** each journal entry has 2..N postings and must satisfy `Σ debits = Σ credits` per currency.
- **Minor-unit money model:** money is stored as integer minor units (`long`) with explicit currency and scale; floating point is not used for monetary values.
- **Append-only ledger:** corrections are expressed through full or partial reversals that reference original entries and cannot exceed them.
- **Tamper-evident entry chain:** journal entries are chained with SHA-256 hashes and exposed through an integrity verification endpoint.
- **Derived balances:** balances are always derivable from postings, with cached-vs-derived reconciliation available as a self-check.

## Technology Stack

- .NET 10 / `net10.0`
- C# and ASP.NET Core Minimal APIs
- EF Core 10 with SQLite by default; Postgres-compatible by design
- JWT bearer authentication with scope-based authorization policies
- OpenTelemetry metrics and tracing
- Serilog structured logging
- xUnit unit and integration tests
- HTTP API on `http://localhost:5013`

## Architecture

Clean/modular monolith with four layers:

1. **Domain:** pure accounting invariants and financial rules with no external dependencies.
2. **Application:** use-case services and concurrency orchestration.
3. **Infrastructure:** EF Core persistence, locks, FX support, metrics, background hold expiration, and reconciliation support.
4. **Api:** ASP.NET Core Minimal API endpoints, authentication, authorization, OpenAPI, and HTTP concerns.

The implementation contains 77 C# files and roughly 5,350 lines of C#.

## What This Demonstrates

- Designing concurrency protocols for correctness, not just throughput.
- Modelling a financial domain with double-entry accounting constraints.
- Building a clean architecture backend where core invariants stay isolated from infrastructure.
- Testing financial failure modes with unit and integration coverage.
- Implementing idempotency, append-only corrections, and tamper-evidence.
- Adding production-style observability with metrics, tracing, structured logs, health checks, and correlation ids.
- Communicating engineering trade-offs honestly for a reference implementation.

## Verification

The test result is real: `dotnet test -c Release` passes 99 tests on .NET 10: 80 unit tests and 19 integration tests, with 0 failed.

Key concurrency tests prove:

- 150 parallel transfers between the same two accounts conserve total value and produce correct derived balances.
- 150 bidirectional transfers never deadlock.
- 50 concurrent withdrawals against limited funds never overdraw; exactly the funded number succeed.
- 50 parallel requests using the same idempotency key produce exactly one posting.

## Honest Scope and Limitations

- This is a self-directed engineering case study, not a deployed banking product.
- Demo data is fictional and uses Example Bank with KES, USD, and EUR examples.
- Docker files may exist, but Docker was unavailable on the build host and was not verified.
- SQLite is the default so the project runs with zero external infrastructure; Postgres is the intended production-style direction.
- JWT uses a single symmetric signing key for demo configuration.
- Adjustments require `ledger:adjust` scope, but no four-eyes maker-checker approval workflow is enforced yet.
- In-process locks are single-node only; a distributed deployment would need database-level locking or a distributed coordination strategy.
