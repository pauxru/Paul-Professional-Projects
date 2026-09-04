# Portfolio Index

Thirty **self-directed engineering case studies**. Root:
`C:\Users\rukwaropaul\Downloads\DEV\Projects`

Every project targets **.NET 10 (`net10.0`)** and is designed to build and test on a machine with
**only the .NET SDK installed** — no Docker, no database server, no paid API keys. See
[`ENVIRONMENT.md`](ENVIRONMENT.md) for why, and [`shared-docs/`](shared-docs/) for the standards
every project is held to.

Conventions: API listens on `5000 + project number`; UI (where present) on `3000 + project number`.
Run any project with:

```powershell
cd <folder>
dotnet build -c Release
dotnet test  -c Release
dotnet run --project src\<Name>.Api      # then open http://localhost:50NN/docs
```

Live build/verification state lives in [`PORTFOLIO_PROGRESS.md`](PORTFOLIO_PROGRESS.md).

---

## The thirty projects

### 01 — Enterprise Order & Payments Platform
`01-enterprise-order-payments-platform` · port 5001
- **Problem:** merchants double-charge customers and lose money to unreconciled settlements when payment flows are not idempotent and asynchronous callbacks are mishandled.
- **Tech:** ASP.NET Core, EF Core/SQLite (Postgres adapter), transactional outbox, in-process bus (RabbitMQ/Service Bus adapters), OpenTelemetry.
- **Skills:** idempotency keys, transactional outbox, provider-timeout recovery, HMAC webhooks, refund lifecycle, settlement reconciliation, distributed tracing.
- **Profile:** Main software engineering · Fintech.

### 02 — Legacy .NET Modernization Lab
`02-legacy-dotnet-modernization` · ports 5002 (modern) / 5102 (legacy)
- **Problem:** an insurance claims system built on 2010-era patterns is unsafe to change and impossible to test.
- **Tech:** deliberately legacy-style ASP.NET Core MVC + raw ADO.NET vs a modern modular .NET 10 target; anti-corruption layer; legacy data importer.
- **Skills:** modernization assessment, characterization testing, strangler-fig planning, compatibility matrix, cutover runbook, data migration validation.
- **Profile:** .NET/Azure modernization.

### 03 — Enterprise RAG Knowledge Assistant
`03-enterprise-rag-knowledge-assistant` · port 5003
- **Problem:** enterprises cannot let staff query internal documents with an LLM without permission leakage, hallucination and unverifiable answers.
- **Tech:** ASP.NET Core, deterministic local embedding + extractive answer providers (optional Azure OpenAI adapter), BM25 + dense hybrid retrieval with RRF, SQLite vector store.
- **Skills:** chunking strategies, hybrid retrieval, permission-aware retrieval, citation spans, grounding checks, refusal thresholds, prompt versioning, retrieval evaluation (Recall@k, MRR, nDCG).
- **Profile:** AI engineering.

### 04 — Multi-Tenant B2B SaaS Platform
`04-multitenant-b2b-saas` · port 5004 · UI 3004
- **Problem:** B2B SaaS fails commercially and legally when tenant isolation, entitlements and billing state are not enforced structurally.
- **Tech:** ASP.NET Core, EF Core global query filters + save-changes interceptor, policy authorization, billing simulator with signed webhooks.
- **Skills:** provable tenant isolation, RBAC, entitlements and quotas, feature flags with percentage rollout, dunning state machine, tenant-scoped caching.
- **Profile:** Main software engineering · SaaS.

### 05 — Financial Reconciliation & Settlement Engine
`05-financial-reconciliation-engine` · port 5005
- **Problem:** finance teams cannot explain why internal transaction records disagree with a provider's settlement file.
- **Tech:** ASP.NET Core, streaming CSV/fixed-width ingestion, data-driven matching ruleset, EF Core/SQLite.
- **Skills:** configurable matching rules, many-to-one matching, fee-adjusted matching, exception queue with four-eyes resolution, idempotent re-runnable runs, large-batch performance work.
- **Profile:** Fintech · Data-intensive.

### 06 — Production Incident Diagnostics Lab
`06-production-incident-diagnostics` · port 5006
- **Problem:** most teams cannot diagnose the ten failure modes that cause the majority of production incidents.
- **Tech:** a sample .NET app with toggleable pathological code paths, a measurement harness, EF command interceptors, GC/ThreadPool sampling.
- **Skills:** N+1, missing indexes, pool exhaustion, memory leaks, sync-over-async, thread starvation, downstream timeouts, retry storms, poison messages, cache stampede — each reproduced, measured and fixed.
- **Profile:** Production troubleshooting · SRE.

### 07 — Secure Zero-Trust API Platform
`07-zero-trust-api-platform` · port 5007
- **Problem:** enterprises expose partner, customer and admin APIs from one codebase without differentiated trust, and get breached through the weakest surface.
- **Tech:** ASP.NET Core, RS256 + JWKS with key rotation, policy authorization, rate limiting, HMAC webhooks, hash-chained audit.
- **Skills:** OAuth2-style flows, refresh-token rotation with reuse detection, scope design, explainable authorization decisions, API-key-to-JWT migration, threat modelling.
- **Profile:** Security · Architecture.

### 08 — Event-Driven Logistics & Fleet Platform
`08-event-driven-logistics-platform` · port 5008 · UI 3008
- **Problem:** fleet telemetry arrives late, duplicated and out of order, and naive pipelines produce wrong ETAs and alert storms.
- **Tech:** ASP.NET Core, partitioned in-process channel pipeline, hand-written geospatial maths (haversine, point-in-polygon, spatial bucketing), EF Core/SQLite.
- **Skills:** event-time vs processing-time, watermarks and late-arrival handling, per-key ordering, deduplication, geofencing with hysteresis, ETA modelling, telemetry replay.
- **Profile:** Distributed systems · IoT.

### 09 — Intelligent Document Processing Platform
`09-intelligent-document-processing` · port 5009
- **Problem:** accounts-payable automation fails when extraction confidence is not routed and validated, silently posting wrong invoices.
- **Tech:** ASP.NET Core, explainable rules/feature classifier, spatial table extraction, deterministic validation rules, human review queue.
- **Skills:** pipeline state machines, confidence routing, three-way match, fuzzy supplier matching, correction feedback loops, straight-through-processing rate as a measured KPI.
- **Profile:** AI engineering · Enterprise automation.

### 10 — Industrial IoT Monitoring Platform
`10-industrial-iot-monitoring` · port 5010
- **Problem:** industrial sites lose telemetry during connectivity outages and cannot trust alerts from noisy sensors.
- **Tech:** a hand-written MQTT 3.1.1 codec + broker in C#, an edge gateway with durable store-and-forward, device twins, ESP32 reference sketch.
- **Skills:** binary protocol implementation, offline buffering and ordered replay, time-series rollups, explainable anomaly detection, safe remote commands, OTA with rollback.
- **Profile:** IoT/Embedded · Distributed systems.

### 11 — High-Scale Notification Delivery Platform
`11-notification-delivery-platform` · port 5011
- **Problem:** notification systems double-send, spam users during quiet hours, and collapse when one provider degrades.
- **Tech:** ASP.NET Core, DB-backed outbox queue with fair per-tenant scheduling, provider simulators with circuit breaking.
- **Skills:** templating with safe escaping, locale fallback, quiet hours and priority lanes, provider failover, dead-letter and replay, suppression/unsubscribe, delivery receipts.
- **Profile:** Main software engineering · Distributed systems.

### 12 — API Integration Hub (iPaaS Lite)
`12-api-integration-hub` · port 5012 (+ simulators 5112/5212/5312)
- **Problem:** enterprise systems do not talk to each other, and bespoke point-to-point integrations rot.
- **Tech:** ASP.NET Core, config-driven REST connectors, a safe (non-eval) mapping expression evaluator, encrypted secret store, in-repo misbehaving target simulators.
- **Skills:** connector abstraction, pagination strategies, OAuth2 token caching, checkpointed resumable runs, contract-drift detection, DLQ replay, SSRF guards, secret redaction.
- **Profile:** System integration · Enterprise architecture.

### 13 — Digital Banking Ledger
`13-digital-banking-ledger` · port 5013
- **Problem:** balance-column accounting silently loses money; only double-entry with enforced invariants is auditable.
- **Tech:** ASP.NET Core, EF Core/SQLite, minor-unit `long` money, ordered locking + optimistic concurrency, hash-chained entries.
- **Skills:** double-entry invariants, holds and partial capture, reversals, FX with gain/loss accounts, interest accrual day-count conventions, statement tie-out, concurrency proofs.
- **Profile:** Fintech · High-reliability software.

### 14 — Loan Origination & Credit Workflow
`14-loan-origination-platform` · port 5014
- **Problem:** lenders need credit decisions that are reproducible and explainable years later, not opaque scores.
- **Tech:** ASP.NET Core, versioned declarative rulesets, transparent weighted scorecard, KYC/bureau/disbursement simulators.
- **Skills:** decision traces, ruleset what-if simulation, amortisation and APR/IRR maths, affordability stress testing, underwriting queues with four-eyes and delegated authority.
- **Profile:** Fintech.

### 15 — Fraud Detection Event Pipeline
`15-fraud-detection-event-pipeline` · port 5015
- **Problem:** risk decisions must be made in milliseconds using stateful history, and must be explainable to analysts and regulators.
- **Tech:** ASP.NET Core, partitioned channel pipeline, in-memory windowed feature store with O(1) updates, versioned rule engine.
- **Skills:** sliding-window aggregates, velocity/impossible-travel/device rules, latency budgets with graceful degradation, case management, analyst feedback loop, champion-challenger shadow mode.
- **Profile:** Fintech · Distributed systems.

### 16 — Subscription Billing & Usage Metering Engine
`16-subscription-billing-engine` · port 5016
- **Problem:** billing bugs are revenue bugs; proration, month-end anchoring and dunning are where SaaS companies lose money and trust.
- **Tech:** ASP.NET Core, EF Core/SQLite, minor-unit money, plan versioning, payment simulator with signed webhooks.
- **Skills:** tiered/volume/graduated pricing, second-accurate proration, idempotent invoice runs, usage metering with late arrivals, dunning and suspension, MRR movement reporting.
- **Profile:** Fintech · SaaS.

### 17 — Distributed Job Scheduler
`17-distributed-job-scheduler` · port 5017
- **Problem:** naive schedulers run the same job twice, or stop running it at all when a node dies silently.
- **Tech:** ASP.NET Core, lease + fencing-token coordination over a shared store, multiple worker processes, hand-written cron parser.
- **Skills:** lease-based ownership, fencing against stale workers, leader election, cron and DST correctness, retry/backoff/DLQ, DAG dependencies, priority aging, graceful drain.
- **Profile:** Distributed systems · DevOps.

### 18 — Feature Flag & Configuration Service
`18-feature-flag-service` · port 5018
- **Problem:** teams cannot ship safely without runtime kill switches and progressive rollout that behaves identically in every service.
- **Tech:** ASP.NET Core + a real .NET client SDK with local evaluation, SSE streaming updates and offline bootstrap.
- **Skills:** targeting rule engines, stable percentage bucketing with stickiness, server↔SDK evaluation parity, prerequisites and cycle detection, approval workflows, stale-flag debt reporting.
- **Profile:** DevOps/Cloud · Platform engineering.

### 19 — Enterprise Audit & Compliance Event Store
`19-enterprise-audit-platform` · port 5019
- **Problem:** an audit log nobody can prove is unaltered has no evidentiary value.
- **Tech:** ASP.NET Core, append-only store with an EF interceptor blocking mutation, per-tenant hash chain, Merkle checkpoints with inclusion proofs.
- **Skills:** canonical serialization, tamper evidence, retention vs immutability reconciliation, legal hold, keyset pagination, signed evidence packs, meta-auditing of reads.
- **Profile:** Security · Architecture · Compliance-adjacent.

### 20 — Secrets Rotation & Credential Management
`20-secrets-rotation-platform` · port 5020
- **Problem:** organisations do not rotate credentials because rotation causes outages; the hard part is the choreography, not the cryptography.
- **Tech:** ASP.NET Core, AES-256-GCM envelope encryption with AAD binding, rotation state machine, consumer acknowledgement tracking, Key Vault adapter interface.
- **Skills:** dual-write vs cutover rotation, verification-gated promotion, automatic rollback, crash-resumable workflows, scheduling with jitter, break-glass with four-eyes, secret redaction proofs.
- **Profile:** Security · DevOps/Cloud.

### 21 — Real-Time Collaboration Backend
`21-realtime-collaboration-platform` · port 5021
- **Problem:** concurrent editing silently corrupts documents unless the convergence model is chosen and implemented correctly.
- **Tech:** ASP.NET Core + SignalR, an implemented OT/CRDT convergence algorithm, snapshot + operation log persistence.
- **Skills:** operational transformation or CRDT convergence with randomised property tests, causality buffering, reconnect/resync, comment anchor rebasing, presence throttling.
- **Profile:** Distributed systems · Main software engineering.

### 22 — Enterprise Search Platform with Hybrid Retrieval
`22-enterprise-search-platform` · port 5022
- **Problem:** search relevance is the product, and teams cannot tune what they do not understand.
- **Tech:** a search engine written from scratch in C# — analyzers, Porter stemmer, positional inverted index, BM25, facets, ANN vector index, RRF hybrid ranking.
- **Skills:** analysis pipelines, scoring maths verified against hand computation, facet counting semantics, alias-based zero-downtime reindex, security trimming, relevance evaluation (nDCG/MRR).
- **Profile:** AI engineering · Data-intensive.

### 23 — Healthcare Appointment & Workflow Platform
`23-healthcare-workflow-platform` · port 5023
- **Problem:** clinic scheduling is a hard constraint problem, and clinical data access must be provably restricted and audited.
- **Tech:** ASP.NET Core, slot-generation engine with timezone/DST correctness, RBAC + ABAC care-relationship authorization, append-only clinical notes.
- **Skills:** constraint-based availability, concurrent booking prevention, break-glass access with alerting, amendment-only clinical records, referral SLA clocks, access-anomaly reporting.
- **Profile:** Main software engineering · Security · Regulated domains.

### 24 — Identity & Access Management Portal
`24-identity-access-management` · port 5024
- **Problem:** enterprises cannot answer "who has access to what, and why" — the question every auditor asks first.
- **Tech:** ASP.NET Core, RBAC + ABAC policy engine with deny precedence, role-hierarchy DAG, provisioning connectors with reconciliation.
- **Skills:** joiner-mover-leaver automation, separation-of-duties detection, JIT privileged elevation with expiry, certification campaigns with auto-revocation, access-derivation-path reporting.
- **Profile:** Security · Enterprise architecture.

### 25 — Data Pipeline & Analytics Lakehouse
`25-data-pipeline-lakehouse` · port 5025
- **Problem:** analytics is untrustworthy without incremental correctness, data-quality gates and lineage.
- **Tech:** C# medallion pipeline with a hand-built Delta-like table format (atomic commits, snapshot isolation, time travel), SQLite serving, DAG orchestrator.
- **Skills:** CDC handling, SCD Type 2 with effective-version joins, idempotent incremental runs, declarative data-quality gates with circuit breaking, column-level lineage and impact analysis, safe SQL serving.
- **Profile:** Data-intensive · DevOps/Cloud.

### 26 — SRE Service Reliability Dashboard
`26-sre-reliability-dashboard` · port 5026
- **Problem:** most reliability dashboards compute burn rate incorrectly and therefore page at the wrong times.
- **Tech:** ASP.NET Core, own time-series store, request-based and window-based SLIs, multi-window multi-burn-rate alerting.
- **Skills:** SLO mathematics derived and unit-tested, error-budget policy with a deploy-gate API, incident MTTA/MTTD/MTTR, blameless postmortems with action tracking, alert-quality analysis.
- **Profile:** SRE · Production troubleshooting.

### 27 — API Performance & Load Testing Toolkit
`27-api-performance-toolkit` · sample API port 5027
- **Problem:** homegrown load tests produce numbers that are statistically meaningless and hide the worst latency.
- **Tech:** a C# load runner with open/closed load models, HdrHistogram-style percentiles, coordinated-omission correction, statistical run comparison; sample API with tunable pathologies.
- **Skills:** load modelling, percentile maths, stress knee detection, soak drift analysis, capacity search, significance testing, CI performance gating.
- **Profile:** Production troubleshooting · DevOps/Cloud.

### 28 — Cloud Deployment Reference Architecture
`28-cloud-deployment-reference` · port 5028
- **Problem:** applications are written without the operational affordances that safe deployment requires.
- **Tech:** a deployment-shaped ASP.NET Core app (migration runner, layered config, three-tier health model, graceful drain) + Bicep and Terraform reference IaC + CI/CD with canary gating.
- **Skills:** managed identity and Key Vault references, blue/green and canary with automated rollback gates, expand/contract migrations, disaster-recovery planning, Well-Architected self-assessment.
- **Profile:** DevOps/Cloud · .NET/Azure.

### 29 — AI Agent Workflow Orchestration Platform
`29-ai-agent-workflow-platform` · port 5029
- **Problem:** agent frameworks give model output far too much authority; production needs bounded, auditable, resumable execution.
- **Tech:** ASP.NET Core, deterministic mock model (optional real providers), typed tool registry with JSON-schema validation, durable workflow engine, replay-based evaluation.
- **Skills:** closed tool allow-lists, prompt-injection resistance, budgets and loop detection, human approval steps, complete execution traces, deterministic replay regression testing.
- **Profile:** AI engineering · Architecture.

### 30 — Cloud Cost & Observability Platform
`30-cloud-cost-observability-platform` · port 5030
- **Problem:** organisations cannot attribute cloud spend to teams or distinguish a real cost anomaly from Tuesday.
- **Tech:** ASP.NET Core, synthetic multi-subscription cost dataset, allocation rule engine, seasonal anomaly detection, backtested forecasting.
- **Skills:** exact cost allocation invariants, shared-cost splitting, showback/chargeback with audit trails, MAPE-backtested forecasting, quantified savings recommendations with realised-savings verification, unit economics.
- **Profile:** DevOps/Cloud · FinOps · Data-intensive.

---

## Capability matrix

| # | Project | Main SWE | .NET/Azure Modernization | AI Engineering | Fintech | Production Troubleshooting | IoT/Embedded | DevOps/Cloud | Architecture |
|---|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| 01 | Order & Payments | ●● | ● | | ●● | ● | | ● | ●● |
| 02 | Legacy Modernization | ● | ●● | | | ● | | ● | ●● |
| 03 | RAG Assistant | ● | | ●● | | | | | ● |
| 04 | Multi-Tenant SaaS | ●● | ● | | ● | | | ● | ●● |
| 05 | Reconciliation Engine | ● | | | ●● | ● | | | ● |
| 06 | Incident Diagnostics | ● | ● | | | ●● | | ● | ● |
| 07 | Zero-Trust API | ● | ● | | ● | | | ● | ●● |
| 08 | Logistics Platform | ●● | | | | ● | ●● | ● | ●● |
| 09 | Document Processing | ● | | ●● | ● | | | | ● |
| 10 | Industrial IoT | ● | | | | ● | ●● | ● | ● |
| 11 | Notification Platform | ●● | | | | ● | | ● | ● |
| 12 | Integration Hub | ● | ●● | | ● | ● | | ● | ●● |
| 13 | Banking Ledger | ●● | | | ●● | | | | ●● |
| 14 | Loan Origination | ● | | | ●● | | | | ● |
| 15 | Fraud Pipeline | ● | | ● | ●● | ● | | | ●● |
| 16 | Billing Engine | ● | | | ●● | | | | ● |
| 17 | Job Scheduler | ●● | | | | ●● | | ●● | ●● |
| 18 | Feature Flags | ● | | | | ● | | ●● | ● |
| 19 | Audit Platform | ● | ●● | | ● | | | ● | ●● |
| 20 | Secrets Rotation | | ●● | | | ● | | ●● | ● |
| 21 | Realtime Collaboration | ●● | | | | ● | | | ●● |
| 22 | Search Platform | ●● | | ●● | | ● | | | ● |
| 23 | Healthcare Workflow | ●● | ● | | | | | | ● |
| 24 | IAM Portal | ● | ●● | | | | | ● | ●● |
| 25 | Data Lakehouse | ● | ● | ● | | | | ●● | ●● |
| 26 | SRE Dashboard | | | | | ●● | | ●● | ● |
| 27 | Performance Toolkit | ● | | | | ●● | | ●● | |
| 28 | Cloud Deployment | ● | ●● | | | ● | | ●● | ●● |
| 29 | AI Agent Platform | ● | | ●● | | | | ● | ●● |
| 30 | Cloud Cost Platform | ● | ● | | ● | ● | | ●● | ● |

`●●` = anchor project for that profile · `●` = supporting evidence.

## Recommended profile line-ups

| Profile | Lead with |
|---|---|
| Main software engineering | 01, 13, 04, 08, 17 |
| .NET / Azure modernization | 02, 28, 12, 24, 19 |
| AI engineering | 03, 29, 09, 22 |
| Fintech / payments | 01, 13, 05, 16, 15 |
| Production troubleshooting / SRE | 06, 26, 27, 17 |
| IoT / embedded | 10, 08 |
| DevOps / cloud / FinOps | 28, 30, 20, 18 |
| Security / architecture | 07, 24, 19, 20 |
