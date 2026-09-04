# Architecture

A deterministic agent-orchestration platform built as a clean 4-layer .NET solution. The guiding
idea: **the workflow engine is deterministic; the model is a bounded participant, never the driver.**

## Layers

```
AgentPlatform.Domain          (no dependencies)
  └─ entities & aggregates (WorkflowRun, StepExecution, TraceEvent, ApprovalTask),
     value objects (Money), rules (RefundEligibilityCalculator), workflow step types,
     budgets, security value objects. Pure, deterministic, no I/O.

AgentPlatform.Application     (→ Domain)
  └─ the orchestration brain: WorkflowEngine (state machine), ToolInvoker (security choke point),
     IChatModel abstraction, ApprovalService, EvaluationHarness, JSON-schema validator,
     safe expression evaluator, condition/loop logic, port interfaces (IRunStore, IApprovalStore,
     IIdempotencyStore, IClock, IFaultInjector …).

AgentPlatform.Infrastructure (→ Application)
  └─ adapters: EF Core persistence (AgentDbContext + stores), the closed tool implementations,
     deterministic transforms, the workflow/prompt catalogs, the model providers
     (DeterministicMockModel default; OpenAI/Azure adapters; ReplayModel), and the DI composition root.

AgentPlatform.Api            (→ Infrastructure)
  └─ ASP.NET Core minimal APIs, JWT auth + scope policies, ProblemDetails, correlation ids,
     rate limiting, OpenTelemetry, health checks, and a static HTML/JS console.
```

Dependencies point inward only. The Domain and Application layers know nothing about EF, HTTP or any
model vendor — which is exactly why the whole thing is testable offline.

## Container view

```mermaid
flowchart TB
    subgraph Client
        UI["Web console (static HTML/JS)"]
        CLI["curl / Invoke-RestMethod / demo.ps1"]
    end

    subgraph API["AgentPlatform.Api (:5029)"]
        EP["Minimal API /api/v1/*"]
        SEC["JWT + scope policies"]
        OTEL["OpenTelemetry + metrics"]
    end

    subgraph APP["Application"]
        ENG["WorkflowEngine (state machine)"]
        INV["ToolInvoker (security choke point)"]
        APR["ApprovalService"]
        EVAL["EvaluationHarness"]
    end

    subgraph INFRA["Infrastructure"]
        TOOLS["Closed tool allow-list (9 tools)"]
        MODEL["IChatModel: DeterministicMockModel (default)\nOpenAI / Azure / Replay"]
        DB[("EF Core + SQLite")]
    end

    UI --> EP
    CLI --> EP
    EP --> SEC --> ENG
    ENG --> INV --> TOOLS
    ENG --> MODEL
    ENG --> APR
    ENG --> DB
    INV --> DB
    APR --> DB
    EVAL --> ENG
    EP --> OTEL
```

## Headline flow — refund approval (with a human pause)

The signature flow: gather evidence, compute eligibility **in deterministic code**, propose the
action, pause for a human, then execute idempotently. The model is not in this loop at all.

```mermaid
sequenceDiagram
    autonumber
    actor Caller
    participant API
    participant Engine as WorkflowEngine
    participant Calc as RefundEligibilityCalculator
    participant Inv as ToolInvoker
    participant DB
    actor Approver

    Caller->>API: POST /api/v1/runs {refund-approval, inputs}
    API->>Engine: StartRun (scope agents:run)
    Engine->>DB: lookup_customer / order (ReadOnly tools)
    Engine->>Calc: compute eligibility (pure code)
    Calc-->>Engine: {eligible, maxRefund, requiresApproval}
    alt ineligible
        Engine->>DB: persist run = Rejected
        Engine-->>API: 201 (Completed / Rejected)
    else eligible
        Engine->>DB: create ApprovalTask (proposed action + args + reasoning)
        Engine->>DB: persist run = WaitingForApproval
        Engine-->>API: 201 (WaitingForApproval)
        Approver->>API: POST /api/v1/approvals/{id}/approve (scope agents:approve)
        API->>Engine: ContinueAfterApproval
        Engine->>Inv: create_refund_request (Mutating, idempotency key)
        Inv->>DB: insert RefundRequest + AuditLog (commit once)
        Inv-->>Engine: result (at-most-once)
        Engine->>DB: persist run = Succeeded
        Engine-->>API: run Completed / Succeeded
    end
```

If the engine crashes between the mutation commit and advancing the run, resume replays the step and
the **idempotency key** returns the cached result instead of inserting a second refund.

## Run state machine

```mermaid
stateDiagram-v2
    [*] --> Pending
    Pending --> Running
    Running --> WaitingForApproval: mutating/external action needs sign-off
    WaitingForApproval --> Running: approved / rejected / timeout-default
    Running --> Completed: terminal (Succeeded / Escalated / Rejected)
    Running --> Failed: non-transient failure / retries exhausted
    Running --> Halted: budget cap / loop detected / wall-clock timeout
    Running --> Cancelled: operator cancels
    Completed --> [*]
    Failed --> [*]
    Halted --> [*]
    Cancelled --> [*]
```

## Trace anatomy

Every run is a totally-ordered sequence of immutable `TraceEvent`s (ordered by `Ordinal`), mirrored
by an OpenTelemetry span tree and replayable via the `ReplayModel`.

```mermaid
flowchart LR
    RS["RunStarted"] --> SS1["StepStarted (get_ticket)"]
    SS1 --> TC1["ToolCall get_ticket ✓ args/result/cost/ms"]
    TC1 --> SS2["StepStarted (classify)"]
    SS2 --> MC["ModelCall promptVersion, messages, tokens, latency"]
    MC --> DEC["Decision branch → auto_resolve / escalate"]
    DEC --> ST["StateTransition"]
    ST --> TCX["ToolCall (blocked) ✗ policy_violation — injection recorded"]
    TCX --> RC["RunCompleted outcome"]
```

Each event carries what you need to explain and reproduce a run: for a model call, the prompt
version, rendered messages, response, tokens and latency; for a tool call, the arguments, result or
structured error, duration and cost; plus every decision and state transition.

## Why this shape

- **Determinism first** — control flow is a validated state machine, so runs are predictable,
  testable, replayable and auditable. The model does bounded language work (classify, summarise) and
  never selects the next step or executes a side effect directly.
- **One security choke point** — every tool call goes through `ToolInvoker`: exists → scope → parse →
  schema-validate/coerce → rate-limit → approval-gate → idempotency → timeout → output-cap.
- **Durable by construction** — persisting per-step is what makes runs resumable; idempotency keys
  make mutating tools at-most-once.
- **Offline by default** — the deterministic mock model means the entire system builds, tests and
  demos with only the .NET SDK.
