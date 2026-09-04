# Architecture

## Context

The hub sits between systems that differ in authentication, pagination, resource shape, availability, and write semantics. The central architectural constraint is that retries and human replay must never create duplicate business effects.

## Containers

```mermaid
flowchart TB
  subgraph Hub["Integration Hub process"]
    API[Minimal API + admin UI]
    Scheduler[Scheduler + retention workers]
    Runner[Flow runner]
    Registry[Connector registry]
    Mapping[Safe evaluator]
    Persistence[EF Core adapters]
    SecretAdapter[AES-GCM secret adapter]
  end
  DB[(SQLite)]
  SecretFile[(Encrypted file)]
  CRM[CRM simulator]
  ERP[ERP simulator]
  Payment[Payments simulator]
  Drop[Drop folder]
  API --> Runner
  Scheduler --> Runner
  Runner --> Registry
  Runner --> Mapping
  Runner --> Persistence --> DB
  Registry --> SecretAdapter --> SecretFile
  Registry --> CRM
  Registry --> ERP
  Registry --> Payment
  Registry --> Drop
```

## Component responsibilities

| Component | Responsibility |
|---|---|
| Domain | Stable connector descriptors, flow language, versioning invariants |
| Application | Ports, mapping evaluator, schema checks, cron, retry/circuit/bulkhead/idempotency/checkpoint primitives |
| Infrastructure | REST/file/webhook adapters, EF stores, encryption, execution orchestration |
| API | Composition, auth policies, rate limits, middleware, endpoints, static operator UI |
| Simulators | Executable test targets with deliberate edge and failure behavior |

## Execution sequence

```mermaid
sequenceDiagram
  participant Trigger
  participant Runner
  participant Source
  participant Mapper
  participant Target
  participant Store
  Trigger->>Runner: run flow version
  Runner->>Store: create run
  Runner->>Source: fetch with auth/pagination
  Source-->>Runner: records
  Runner->>Mapper: validate, detect drift, transform
  loop from checkpoint
    Runner->>Store: find stable idempotency result
    alt not seen
      Runner->>Target: load + Idempotency-Key
      Target-->>Runner: outcome
      Runner->>Store: result / DLQ + checkpoint
    else already seen
      Store-->>Runner: original result
    end
  end
  Runner->>Store: redacted step snapshots + final status
```

## Failure domains

- A connector owns its timeout, retry, circuit, and bulkhead state.
- A step owns its configured timeout and retry budget.
- A record-level non-transient load failure is quarantined; it does not abort sibling records.
- A process interruption leaves the checkpoint at the first uncompleted index.
- A replay reuses the original record key and is safe after ambiguous responses.
- Contract drift creates alerts before changed data can be silently mapped.

## Deployment boundary

The demonstrator is a modular monolith plus three simulator executables. A production multi-instance hub would move SQLite to a server database, secret values to a managed vault, schedule state to durable leases, and telemetry to OTLP. Splitting the runner into workers is optional and should follow load evidence rather than precede it.
