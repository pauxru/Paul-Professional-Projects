# Architecture — Production Incident Diagnostics Lab

## Intent
The lab separates a runnable subject system from intentionally pathological scenarios so that diagnostics code remains useful outside a single lesson. The Northstar Logistics API follows `Api → Infrastructure → Application → Domain`; the harness consumes `Scenarios → Diagnostics`.

```mermaid
flowchart TB
    subgraph Subject system
      API[Lab.SampleApp]
      INFRA[Lab.Infrastructure]
      APP[Lab.Application]
      DOMAIN[Lab.Domain]
      API --> INFRA --> APP --> DOMAIN
      API --> APP
      INFRA --> SQLITE[(SQLite)]
    end
    subgraph Lab
      HARNESS[Lab.Harness] --> SCENARIOS[Lab.Scenarios] --> DIAGNOSTICS[Lab.Diagnostics]
      SCENARIOS --> LABSQL[(in-memory SQLite)]
      DIAGNOSTICS --> REPORTS[JSON / Markdown evidence]
    end
```

## Scenario run sequence
```mermaid
sequenceDiagram
    participant R as Reviewer
    participant H as Harness
    participant C as ScenarioCatalog
    participant I as Incident
    participant M as MeasurementSession
    participant W as ScenarioReportWriter
    R->>H: --scenario INC-008 --mode fixed
    H->>C: resolve incident ID
    C->>I: RunAsync(options, cancellation token)
    I->>M: capture before snapshot
    I->>I: execute bounded workload
    I->>M: capture after snapshot
    I-->>H: ScenarioReport
    H->>W: write JSON and Markdown
    W-->>R: evidence paths
```

## Boundary rules
- `Lab.Domain` has no I/O or framework dependency.
- `Lab.Application` owns use-case contracts and ports.
- `Lab.Infrastructure` owns EF Core and SQLite adapters.
- `Lab.SampleApp` owns HTTP, authentication, composition, and telemetry wiring.
- `Lab.Diagnostics` is infrastructure-neutral except for the EF interception abstraction it deliberately exposes.
- `Lab.Scenarios` can use an in-memory SQLite connection for the database scenarios but does not host the API.

## Configuration
`DatabaseOptions` and `JwtOptions` are data-annotation validated at startup. `Database:Provider` currently accepts only SQLite, intentionally enforcing the standalone build contract. A Production host refuses the known development signing key.
