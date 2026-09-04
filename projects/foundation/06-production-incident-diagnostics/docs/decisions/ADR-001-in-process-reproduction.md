# ADR-001: Use in-process reproductions for the default lab

## Context
The lab must build, test, and demonstrate failures on a Windows host without Docker, a message broker, Postgres, Redis, or cloud dependencies. Many failure classes normally emerge from distributed infrastructure, but a portfolio reviewer needs a repeatable first run.

## Options
1. Require Docker Compose with database, broker, cache, and dependency simulators.
2. Mock every dependency and assert only control flow.
3. Use in-process simulations backed by real .NET primitives and SQLite where relevant.

## Decision
Use bounded in-process simulations as the default. EF Core scenarios use SQLite and a real `DbCommandInterceptor`; timeout scenarios use `HttpClient` with an in-process handler; queues, workers, and cache coalescing use real `Channel`, `SemaphoreSlim`, `Task`, and collection primitives.

## Consequences
The suite is portable and every scenario can run in CI. Evidence is directly linked to code and does not depend on a local daemon. The trade-off is that some production signatures—TCP saturation, managed broker locks, and server database pool errors—are modelled rather than reproduced with their vendor-specific operational surface.

## Risks
A reader may overgeneralize an in-process latency measurement to a production cluster. Reports and README limitations must label the simulations and focus conclusions on causal structural signals.

## Alternatives
An optional container or cloud profile could add Postgres, Redis, and a broker later, but it cannot replace the standalone default under the current host contract.
