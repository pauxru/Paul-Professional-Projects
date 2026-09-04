# ADR-003: Choose EF Core for the target persistence adapter

## Context
The legacy system uses hand-rolled ADO.NET. The modern target needs constraints, migrations, aggregate-oriented persistence, indexed queries, and optimistic concurrency while remaining runnable on SQLite.

## Options
1. Continue raw ADO.NET.
2. Use Dapper with hand-authored SQL/migrations.
3. Use EF Core with explicit configurations and provider adapters.

## Decision
Choose EF Core. `NorthstarDbContext` maps every aggregate explicitly, uses unique indexes/check constraints, and marks `Claim.Version` as a concurrency token. SQLite is default; Npgsql can be selected by typed configuration.

## Consequences
The target reduces query interpolation risk and makes persistence constraints visible in source. Query shape must still be reviewed to avoid inefficient loading; EF does not remove data-access responsibility.

## Risks
Provider differences and migration SQL need validation against the enterprise production database. Decimal and date behavior should be part of that validation.

## Alternatives
Dapper is reasonable for query-heavy, well-governed SQL teams, but would not itself demonstrate the aggregate/migration consistency desired by this reference implementation.
