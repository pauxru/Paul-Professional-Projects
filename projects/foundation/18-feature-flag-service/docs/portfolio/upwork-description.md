# Upwork Description

Feature Flag & Dynamic Configuration Service — self-directed engineering case study

Problem: delivery teams need to release changes gradually and reverse unsafe behavior without turning every request into a remote configuration dependency.

Built: a .NET 10 feature-flag control plane with SQLite persistence, audit/revert and production approval workflow, plus a local-evaluation .NET SDK with SSE/polling refresh, disk cache, offline defaults, and bounded analytics buffering.

Engineering focus: deterministic cross-SDK bucketing, sticky percentage expansion, explainable targeting reasons, emergency kill-switch governance, and zero-infrastructure integration testing.

Stack: .NET 10, ASP.NET Core, EF Core/SQLite, SSE, JWT, OpenTelemetry, xUnit.

Verification: 72 automated tests run locally. This is a self-directed portfolio project, not client work.
