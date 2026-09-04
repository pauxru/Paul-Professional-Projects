# Architecture

## One-page overview

Modular monolith. Clean architecture layering:

- **`FraudPipeline.Domain`** — pure C#. Entities, value objects, feature aggregator, rule
  definitions. No I/O, no async, no framework references. Pure functions and small classes with
  invariants in constructors.
- **`FraudPipeline.Application`** — services, ports (interfaces), the rule engine, the scoring
  service, the detection evaluator. Depends on `Domain` only.
- **`FraudPipeline.Infrastructure`** — EF Core, SQLite, JWT issuer, clock. Adapters that satisfy
  the ports in `Application`.
- **`FraudPipeline.Api`** — the composition root. Minimal APIs, middleware, endpoint groups.
  Only project that references `Microsoft.AspNetCore.*`.

## Container view

See the README's Mermaid diagram for the container view.

## Sequence — score

```mermaid
sequenceDiagram
  autonumber
  participant API
  participant SS as ScoringService
  participant FS as FeatureStoreService
  participant RE as RuleEngine
  participant RS as IRulesetRepository
  participant TR as ITransactionRepository
  participant DB as SQLite

  API->>SS: ScoreAsync(txn)
  SS->>RS: GetActiveAsync()
  RS->>DB: SELECT rulesets WHERE IsActive
  DB-->>RS: row
  RS-->>SS: Ruleset (JSON)
  SS->>SS: RulesetSerializer.Deserialize
  SS->>FS: Snapshot(txn, now)
  FS->>FS: (in-memory) aggregate 1m/5m/1h/24h/7d
  FS-->>SS: FeatureVector
  SS->>TR: ListRecentByCustomerAsync
  TR->>DB: SELECT ... ORDER BY OccurredAt DESC
  DB-->>TR: rows
  TR-->>SS: List<Transaction>
  SS->>RE: Evaluate(def, inputs)
  RE-->>SS: List<RuleFiringResult>
  SS->>SS: Aggregate (weighted, allow/deny, merchant policy)
  SS->>SS: Check latency budget
  SS->>DB: INSERT scoring_decisions
  SS-->>API: ScoreResult
```

## Sequence — ingest + async consume

```mermaid
sequenceDiagram
  autonumber
  participant Producer
  participant BUS as PartitionedTransactionBus
  participant CONS as TransactionConsumer
  participant FS
  participant SS as ScoringService
  participant DB

  Producer->>BUS: WriteAsync(txn)
  Note over BUS: PartitionOf(cardId) via FNV-1a
  BUS->>CONS: ChannelReader.WaitToReadAsync
  CONS->>DB: AddAsync + SaveAsync
  CONS->>FS: Observe(txn)
  CONS->>SS: ScoreAsync(txn)
  Note over CONS: On failure -> IDeadLetterRepository
```

## Case lifecycle

```mermaid
stateDiagram-v2
  [*] --> New
  New --> Assigned: Assign(analyst)
  Assigned --> UnderInvestigation: any activity
  UnderInvestigation --> Disposed: ProposeDisposition (FalsePositive / Inconclusive)
  UnderInvestigation --> Disposed: ProposeDisposition ConfirmedFraud and exposure < 10k
  UnderInvestigation --> AwaitingApproval: ProposeDisposition ConfirmedFraud and exposure >= 10k
  AwaitingApproval --> Disposed: Approve(other_analyst)
```

## Key design invariants

1. **`Domain` has no I/O.** The feature aggregator is pure logic; the clock is injected. This is
   what makes deterministic testing with `FakeClock` possible.
2. **`Application` depends only on `Domain`.** Ports are defined here; adapters in `Infrastructure`
   implement them.
3. **Ruleset immutability.** Once a `Ruleset` has been activated, its `DefinitionJson` is not
   edited. To change rules, add a new version.
4. **Per-entity ordering by partition hash.** Same key → same partition → same reader.
5. **Reproducible decisions.** Same input + same ruleset version = same output. Enforced by
   `EvaluateOnly` being a pure function of its arguments.

## Where extension points are

- `IRulesetRepository`, `ITransactionRepository`, etc. — swap SQLite for Postgres or Redis by
  adding a new adapter.
- `ITokenIssuer` — swap `DevTokenIssuer` for an OIDC-backed issuer in production.
- `IIpReputationSource` (not yet a port; currently a static list) — a natural next port to extract.
- `FeatureStoreRuntime` — could be replaced with a Redis-backed runtime while keeping the
  `FeatureStoreService` unchanged.
