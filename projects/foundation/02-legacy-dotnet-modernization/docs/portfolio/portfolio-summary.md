# Portfolio Summary

## Northstar Insurance Claims Modernization Lab
Self-directed engineering case study demonstrating a practical .NET modernization engagement. The repository contains a runnable legacy-style claims MVC application and a modern .NET 10 modular API, connected by assessment, characterization, anti-corruption import, strangler routing, reconciliation, and cutover artifacts.

## Engineering signal
- Identifies and proves a real legacy SQL injection flaw rather than merely listing smells.
- Pins financially relevant calculation behavior before refactoring it.
- Separates domain invariants from HTTP, persistence, files, and configuration.
- Designs a coexistence and rollback path rather than assuming a rewrite can be switched on safely.
- Verifies target behavior using SQLite-only automated tests.

## Stack
.NET 10, ASP.NET Core MVC/Minimal APIs, EF Core, SQLite, Npgsql configuration adapter, JWT bearer policies, OpenTelemetry, xUnit, and `WebApplicationFactory`.

## Honest scope
Fictional data only. This is a reference implementation, not client work, a production deployment, or a compliance claim.
