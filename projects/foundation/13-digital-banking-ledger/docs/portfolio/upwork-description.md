# Example Bank — Digital Banking Ledger

## Headline

I built a .NET 10 digital banking ledger case study that demonstrates how to design a correctness-first backend for financial systems where money must never be created, lost, double-posted, or silently overwritten.

This is a self-directed engineering case study, not client work or a deployed product.

## The Problem It Solves

Banking, payments, wallet, and marketplace systems need more than CRUD over balances. They need an append-only ledger that can explain every balance, reject unbalanced entries, handle retries safely, and remain correct when multiple transactions hit the same accounts at the same time.

The core business rule behind this project is: it must be impossible to create money.

## What Was Built

I built Example Bank — Digital Banking Ledger, a reference implementation of a double-entry accounting ledger / core-banking ledger engine. It includes:

- Chart of accounts with asset, liability, equity, income, and expense account types.
- Parent/child account hierarchy with roll-up and control accounts.
- Journal entries with 2..N postings, value date, correlation id, and entry types including transfer, fee, interest, adjustment, reversal, FX conversion, and settlement.
- Transfers with available-balance and configurable overdraft checks.
- Holds/authorizations with full or partial capture, release, and auto-expiration.
- Full and partial reversals that reference original entries and cannot exceed them.
- Fee schedules, interest accrual, FX conversion, statements, trial balance, reconciliation checks, and hash-chain integrity verification.

## The Hard Engineering

The interesting part of the project is correctness under concurrency.

The ledger uses per-account in-process locks acquired in ascending account-id order, then a global hash-chain lock, plus optimistic-concurrency `Version` columns, serialized transactions, bounded retry, and idempotent replay. This prevents deadlocks, protects account-level updates, and ensures duplicate client retries do not create duplicate postings.

The financial model uses double-entry invariants: every journal entry must balance per currency, corrections are append-only reversals, and balances are derivable from postings. Money is stored as integer minor units (`long`) with explicit currency and scale, avoiding floating-point monetary errors.

For tamper evidence, entries are linked through a SHA-256 hash chain with an integrity verification endpoint. This is not a replacement for real operational controls, but it demonstrates how unauthorized mutation of ledger history can be detected.

## Tech Stack

- .NET 10 / C#
- ASP.NET Core Minimal APIs
- EF Core 10 with SQLite by default, Postgres-compatible by design
- JWT bearer authentication with scope-based policies
- OpenTelemetry metrics and tracing
- Serilog structured logging
- xUnit unit and integration tests

The API runs locally on `http://localhost:5013` with zero external infrastructure required by default.

## Verification

The real test result is 99 passing tests on .NET 10 using `dotnet test -c Release`: 80 unit tests and 19 integration tests, 0 failed.

The test suite includes concurrency scenarios for parallel transfers, bidirectional transfers without deadlock, concurrent withdrawals without overdraft, and duplicate idempotency-key requests producing exactly one posting.

## I Can Build This for You

If you need a fintech, payments, wallet, marketplace, accounting, or core-banking backend, I can help design and build systems with the same engineering priorities shown here: correct money movement, idempotent APIs, clean architecture, auditability, secure authorization, observable operations, and testable failure handling.

This portfolio project is evidence of capability and engineering approach. A real client implementation would be adapted to your domain rules, operational controls, database, deployment environment, compliance requirements, and approval workflows.
