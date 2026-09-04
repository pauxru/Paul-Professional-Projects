# Twelve-Factor Conformance

| Factor | Implementation | Status / deliberate gap |
|---|---|---|
| I. Codebase | One repository and one tracked codebase | Satisfied |
| II. Dependencies | NuGet dependencies declared in projects; SDK version documented | Satisfied; no lock file |
| III. Config | Typed config outside code, environment/user-secrets/Key Vault precedence | Satisfied |
| IV. Backing services | DB/cache/bus selected behind ports/options | Satisfied for configured adapters |
| V. Build, release, run | CI artifacts, immutable image tag, separate migration job, revision run | Satisfied by authored workflow; cloud run unverified |
| VI. Processes | Stateless HTTP replicas; state in DB/cache/bus | Satisfied |
| VII. Port binding | Kestrel binds `ASPNETCORE_URLS`; local port 5028 | Satisfied |
| VIII. Concurrency | Horizontal Container Apps replicas; bounded worker loop | Satisfied; API and worker are coupled |
| IX. Disposability | Fast startup state, graceful drain, cancellation-safe checkpointing | Satisfied and tested |
| X. Dev/prod parity | Same .NET/EF code; SQLite locally and PostgreSQL in Azure | Partial by design; provider semantics differ |
| XI. Logs | Structured stdout/OpenTelemetry logs, no file log dependency | Satisfied |
| XII. Admin processes | Migration runner is a one-off process/job | Satisfied |

The deliberate SQLite/PostgreSQL difference is accepted to keep local validation infrastructure-free. A production team should add disposable PostgreSQL integration tests in a Docker-capable CI environment.
