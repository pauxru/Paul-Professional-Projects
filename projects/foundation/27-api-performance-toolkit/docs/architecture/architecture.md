# Architecture

This document is a longer-form partner to the README's Architecture section. Read that first.

## Container view

```mermaid
flowchart LR
    subgraph LR["loadrun (CLI)"]
        CLI[Program.cs<br/>arg parser]
        RUN[RunCommand]
        CMP[CompareCommand]
        RPT[ReportCommand]
    end
    subgraph CORE["LoadRunner.Core"]
        SCN[Scenarios: JSON + validation + templating + CSV feeder]
        HTTP[Http: HttpRequestExecutor + StubExecutor]
        LM[LoadModels: Closed / Open / Ramping / Spike / Stress / Capacity]
        MET[Metrics: MetricsCollector + RequestSample]
        STAT[Statistics: LatencyHistogram + ExactPercentiles + SignificanceTest]
        ANA[Analysis: KneeDetector + SoakDriftDetector + CapacityBinarySearch]
        AS[Assertions: ThresholdEvaluator]
        RES[Results: RunResult + RunResultStore]
        EXE[Execution: ScenarioRunner]
    end
    subgraph REP["LoadRunner.Reporting"]
        SVG[Svg: Line / DualLine / Bars / Overlay]
        HTM[Html: HtmlReport single-file]
        MD[Markdown: MarkdownReport + ComparisonReportBuilder]
    end
    subgraph API["SampleApi (target under test)"]
        WEB[Minimal API on Kestrel<br/>port 5027]
        DB[(SQLite<br/>sampleapi.db)]
        PATH[PathologyState<br/>6 tunable modes]
    end

    CLI --> RUN --> EXE
    CLI --> CMP --> RES
    CLI --> RPT --> HTM
    EXE --> SCN
    EXE --> LM
    LM --> HTTP
    HTTP -->|HTTP| WEB
    HTTP --> MET
    MET --> STAT
    EXE --> AS
    EXE --> ANA
    EXE --> RES
    RES --> HTM
    RES --> MD
    HTM --> SVG
    MD --> SVG
    WEB --> DB
    WEB -.pathology headers.-> PATH
```

## The load-model dispatch

```mermaid
flowchart TB
    S[Scenario.Load.Model]
    S -->|ConstantVUs| CM[ClosedModel]
    S -->|RampingVUs| RCM[RampingClosedModel]
    S -->|ConstantArrivalRate| OM[OpenModel]
    S -->|RampingArrivalRate| ROM[RampingOpenModel]
    S -->|Spike| SPK[ScenarioRunner.RunSpikeAsync<br/>→ RampingClosedModel]
    S -->|Soak| SO[ClosedModel long-run]
    S -->|Stress| STR[ScenarioRunner.RunStressAsync<br/>step OpenModel + KneeDetector]
    S -->|CapacitySearch| CAP[CapacityBinarySearch<br/>+ OpenModel per probe]
    OM --> CH[Channel<intended-start>]
    CH --> WP[Worker pool of N VUs]
    CM --> VU[N VirtualUsers]
    RCM --> VU
    WP --> EX[IHttpRequestExecutor]
    VU --> EX
```

Every dispatch ends in the same executor interface, so a `StubExecutor` in tests exercises
the exact same load-model code paths that the CLI uses against real Kestrel.

## Sequence: open-model run with coordinated-omission tracking

```mermaid
sequenceDiagram
    autonumber
    participant CLI as loadrun run
    participant SR as ScenarioRunner
    participant OM as OpenModel
    participant CH as Channel
    participant WP as Worker pool
    participant EX as HttpRequestExecutor
    participant API as Target API
    participant MC as MetricsCollector
    CLI->>SR: RunAsync(scenario)
    SR->>OM: RunAsync(context)
    Note over OM: scheduler task started
    loop every 1/rate s (wall clock)
        OM->>OM: compute intendedStart
        OM->>CH: push intendedStart
    end
    par worker
        WP->>CH: pull intendedStart
        WP->>WP: if now() < intendedStart, sleep
        WP->>EX: ExecuteAsync(step, intendedStart)
        EX->>API: HTTP request
        API-->>EX: HTTP response
        EX-->>WP: RequestSample<br/>(service + intended latencies)
        WP->>MC: Record(sample)
    end
    Note over MC: aggregates into per-endpoint<br/>and total histograms, plus<br/>per-second time series
    CLI->>SR: (deadline reached)
    SR->>MC: Snapshot(runStart, runEnd, warmUp)
    SR-->>CLI: RunResult with assertions
```

## Data flow through the metrics pipeline

```
per-request sample ─► endpoint LatencyHistogram (service)
                    ├► endpoint LatencyHistogram (intended)
                    ├► aggregate LatencyHistogram (service)
                    ├► aggregate LatencyHistogram (intended)
                    ├► per-endpoint error counter by ErrorKind
                    ├► per-endpoint response-byte counter
                    └► per-second time-series bucket
```

`MetricsCollector` is thread-safe: histograms use interlocked increments on the count array.
No locks on the hot path.

## Assertions and exit codes

```
scenario.assertions ─► ThresholdEvaluator.Evaluate(snapshot)
                         ├─ latency.p50 / p75 / p90 / p95 / p99 / p99.9
                         ├─ intended.p50 / p95 / p99
                         ├─ throughput.rps
                         ├─ error_rate
                         └─ count
                       ► AssertionResult[]
                       ► all pass?  → CLI exit 0
                       ► any fail?  → CLI exit 1
```

## Directory structure

```
src/
  LoadRunner.Core/          class library — engine
  LoadRunner.Reporting/     class library — SVG/HTML/Markdown
  LoadRunner.Cli/           console app — outputs loadrun.dll (AssemblyName)
  SampleApi/                ASP.NET Core Minimal API on port 5027

tests/
  LoadRunner.UnitTests/         xUnit — 63 tests, no I/O
  LoadRunner.IntegrationTests/  xUnit — 9 tests, WebApplicationFactory + SampleApi in-proc

scenarios/                  JSON scenarios
results/                    persisted RunResults (JSON) and reports (HTML + MD)
docs/
  adr/                      ADR-001..005
  architecture/             this file
  runbooks/                 runbooks
  security/                 security review
  portfolio/                portfolio artefacts
  case-study-optimisation.md
  methodology.md
  test-results.md
k6-scripts/                 k6 scripts (NOT executed on this host)
scripts/                    demo.ps1
.github/workflows/          ci.yml + perf.yml
```

## Deployment / distribution

- The CLI is a portable `.dll`; on Windows, `dotnet loadrun.dll` runs it. `dotnet publish
  -c Release -r <rid> --self-contained true` produces a single-file executable per RID.
- The SampleApi is designed as a demonstration target, not a production service — but it
  runs fine as a standalone kestrel app under a service manager if you want a permanent
  practice target.
