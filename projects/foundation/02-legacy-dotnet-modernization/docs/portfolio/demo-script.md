# Demo Script

## Setup
1. Run `dotnet build -c Release` and `dotnet test -c Release`.
2. Start modern API on port 5002 and legacy MVC on port 5102 in separate terminals.
3. Open legacy MVC claims list and modern OpenAPI JSON.

## Five-minute narrative
1. **Show the as-is system.** Navigate to legacy claims, point out `AppSettings.xml`, static `DatabaseHelper`, and fat controller. State that this is a safe local simulation on .NET 10, not actual Framework 4.x.
2. **Show the risk concretely.** Open the characterization test that uses `does-not-exist' OR 1=1 --`; it proves the concatenated legacy filter returns all rows.
3. **Show preservation.** Open shared modernization tests: the same settlement data runs through legacy and pure modern calculator with equivalent output, including intentionally preserved zero/negative quirks.
4. **Show the target boundary.** Walk from API endpoint to application service, domain `Claim` state graph, EF mapping/concurrency token, and `IDocumentStore`.
5. **Exercise API.** Obtain a development token, list claims with `claims:read`, submit with `claims:adjust`, then explain why a stale assessment gets `409`.
6. **Show migration readiness.** Open importer test/report format and strangler/cutover documents. Explain one-writer-per-reference and why importer/freeze avoids dual-write ambiguity.

## Suggested questions
- Why not big bang? Behaviour and data reconciliation risk.
- Why EF Core? Explicit constraints/migrations/concurrency while retaining SQLite verification.
- What changes for production? OIDC, secret store, Blob, malware scanning, audit log, managed DB, facade deployment testing.
