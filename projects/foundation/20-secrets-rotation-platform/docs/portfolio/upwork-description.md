# Upwork Portfolio Description

**Secrets rotation and credential lifecycle platform — self-directed engineering case study**

Problem: shared application credentials are difficult to rotate safely when many consumers cache
them and downstream systems have different cutover constraints.

Built: a .NET 10 and SQLite control plane for hierarchical secret registration, AES-GCM envelope
encryption, versioned references, scheduling, consumer notification/acknowledgement, verified
promotion, rollback, revocation, audit, anomaly reporting, and four-eyes emergency access.

Engineering focus: ciphertext-to-metadata binding with AAD, master-key DEK re-wrapping, persisted
resumable state transitions, dual-write versus maintenance-window strategies, safe failure
recovery, JWT/path authorization, and secret-safe observability.

Stack: C#/.NET 10, ASP.NET Core minimal APIs, EF Core/SQLite, OpenTelemetry, xUnit, HTML/JavaScript.

Verification: Release build and automated unit/integration tests run without Docker, cloud
resources, or external services. Docker and Azure mappings are explicitly unverified design
artifacts.

This is a self-directed portfolio project, not client work.
