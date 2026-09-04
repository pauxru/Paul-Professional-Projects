# Upwork Description

**Identity Governance & Access Administration Portal — self-directed engineering case study**

Problem: Organizations need to answer who has access, why it exists, whether it violates policy, and whether target systems match approved state.

Built: A .NET 10 identity governance reference implementation covering lifecycle automation, hierarchical roles, dynamic groups, explainable RBAC/ABAC, separation of duties, multi-stage approvals, just-in-time privilege, certification campaigns, provisioning reconciliation, audit, reports, SCIM-shaped APIs, and an admin UI.

Engineering focus:

- deny-wins policy decisions with full evaluation traces;
- cycle detection and deep transitive role derivations;
- manager/owner/security workflows with parallel stages, delegation, and escalation;
- fake-clock proof that JIT and overdue campaign access is actually removed;
- orphan/rogue-grant reconciliation, retries, and quarantine;
- “why does this user have access?” evidence through nested roles and dynamic groups.

Stack: ASP.NET Core / .NET 10, EF Core, SQLite, JWT, OpenTelemetry, xUnit.

Verification: Offline Release build and comprehensive SQLite-backed tests; exact output is committed in `docs/test-results.md`.

This is a self-directed portfolio project, not client work. Docker configuration is authored but was not verified on the build host.
