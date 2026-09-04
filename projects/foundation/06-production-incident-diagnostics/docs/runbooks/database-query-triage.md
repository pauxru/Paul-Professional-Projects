# Runbook: Database Query and Index Triage

## Scope
Use for increasing database duration, EF command volume, or connection wait alerts. Relevant lab incidents: `INC-001`, `INC-002`, and part of `INC-003`.

## Immediate safeguards
1. Do not run a broad ad hoc query on the primary while it is saturated.
2. Select a representative correlation ID and endpoint.
3. Cap any diagnostic query by time and result count; use a replica when available.
4. Record the release version, query shape, tenant/partition, and timestamps.

## Diagnose
1. Compare requests/sec, error rate, p95/p99, commands/request, and connection wait.
2. Capture the SQL shape without customer values or secrets.
3. Run the database engine’s plan command for the exact parameterized predicate.
4. Distinguish `SCAN`/table read from an index seek/search, bad estimate, lock wait, and pool wait.
5. Check whether endpoint code loads children inside a loop rather than projecting or batching.

## Mitigate
- For a query fan-out, deploy a projection or bounded include/batch query after verifying cardinality.
- For a missing lookup index, validate write cost and plan on representative data before adding a migration.
- For pool wait, find undisposed leases/readers/transactions and set a finite acquisition timeout; do not only increase pool size.

## Verification
Compare the same endpoint and predicate before/after: commands per request, plan text, p95, error count, and pool wait. Add a regression test with a command interceptor or plan assertion.

## Lab command
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-001 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-002 --mode fixed --requests 20
```
