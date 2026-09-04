# Synthetic Performance Measurements

## Scope
These are actual local, in-process measurements from this repository, not a production capacity claim. The benchmark creates a fresh 5,000-document fictional corpus with ten product-topic clusters, builds the custom index, warms the IVF cluster assignment once, compares a top-10 deterministic vector query, then executes 100 keyword searches.

## Exact command
Executed from the repository root on 2026-09-03 with Windows and .NET SDK 10.0.400 / runtime 10.0.11:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Logging__LogLevel__Microsoft_EntityFrameworkCore = "Warning"
$env:Logging__LogLevel__Default = "Warning"
dotnet run --project src\EnterpriseSearch.Api\EnterpriseSearch.Api.csproj -c Release --no-build -- --benchmark
```

## Real output

```text
Documents:              5000
IndexingTime:           00:00:00.4506827
DocumentsPerSecond:     11094.279855871991
Vector RecallAtK:       1.0 (k=10)
ExactLatency:           00:00:00.0039905
ApproximateLatency:     00:00:00.0006397
ExactCandidates:        5000
ApproximateCandidates:  1000
HundredKeywordQueries:  00:00:00.5611720
```

## Interpretation

| Measurement | Result | Interpretation |
|---|---:|---|
| Build 5,000 documents | 450.683 ms | ~11,094 synthetic documents/s in this process and corpus shape. |
| Exact cosine top-10 | 3.991 ms / 5,000 candidates | Correctness baseline scans every vector. |
| Warmed clustered IVF top-10 | 0.640 ms / 1,000 candidates | This query examined one fifth of the vectors and retained 1.0 top-10 overlap. |
| 100 keyword searches | 561.172 ms total | 5.612 ms mean per sequential local query. |

The result excludes the one-time IVF build warm-up because a deployed implementation should build/refresh ANN structures off the request critical path. The benchmark's controlled topic clusters make recall favourable; evaluate a heterogeneous held-out corpus before selecting probes or declaring an ANN service-level objective.

## How to reproduce or vary
`SearchBenchmark` uses the same `InvertedIndex`, `Bm25Scorer`, `SearchEngine`, exact cosine index, and clustered IVF index as API requests. Change topic distribution, vector dimension, maximum clusters, probe count, or query mix in `SearchBenchmark.cs`, run the command again, and replace these figures rather than extrapolating them.
