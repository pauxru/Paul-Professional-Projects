# Interview Talking Points

## Business problem
Modernization is not a code conversion exercise: policy/claim behaviour, data ownership, and rollback are the real risks. This lab turns that into executable artifacts.

## Architecture choice
I chose a modular monolith rather than microservices. Claims, policy terms, settlement, documents, and workflow benefit from local consistency, and a single deployable reduces migration operational load.

## Failure and recovery
The modern API detects stale writes with a version token and returns 409. The importer rejects invalid rows with reasons instead of coercing them. The cutover runbook makes the facade switch and evidence preservation explicit.

## Security
The legacy injection flaw is deliberate, marked, assessed, and covered by a proof test. The target replaces it with EF parameterization and adds JWT policy scopes, headers, rate limiting, content constraints, and correlation.

## Testing
Characterization tests are the first modernization tool: they establish settlement parity before refactoring. Integration tests use a held-open SQLite in-memory connection, so no external service is required.

## Scale and next steps
For a production deployment, I would add OIDC, managed secrets, database provider validation, Blob/malware scanning, immutable audit records, a real facade, distributed limiting, and workload tests. I would not claim those capabilities are present until verified.
