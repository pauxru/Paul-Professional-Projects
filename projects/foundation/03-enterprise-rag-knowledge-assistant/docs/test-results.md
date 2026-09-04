# Test Results

Last verified: build + test executed together on a Windows host with
`.NET SDK 10.0.400`, `net10.0` target, no Docker, no network, no paid API keys.

## Build

```
> dotnet build -c Release --no-incremental
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.84
```

Projects that built (all 7):

- `RagAssistant.Domain`
- `RagAssistant.Application`
- `RagAssistant.Infrastructure`
- `RagAssistant.Api`
- `RagAssistant.Eval`
- `RagAssistant.UnitTests`
- `RagAssistant.IntegrationTests`

Full log lives in `docs/raw-build.txt`.

## Tests

```
> dotnet test -c Release

Passed!  - Failed:     0, Passed:    56, Skipped:     0, Total:    56, Duration: 193 ms - RagAssistant.UnitTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: 718 ms - RagAssistant.IntegrationTests.dll (net10.0)
```

**70 tests total, 0 failures.** Full log in `docs/raw-test-run.txt`.

## Test coverage areas (70 tests)

### Unit tests (56)

| Area                                      | # tests |
|-------------------------------------------|--------:|
| Access control list & permission logic    |       6 |
| ACL post-filter                           |       2 |
| Chunking — fixed size                     |       6 |
| Chunking — sentence aware                 |       5 |
| Deterministic embedding                   |       5 |
| BM25 index                                |       5 |
| Reciprocal Rank Fusion                    |       5 |
| Grounding / faithfulness checker          |       5 |
| Template chat model (incl. chunk-rank)    |       4 |
| Answering service citation-marker parsing |       3 |
| Contextual query rewriter                 |       3 |
| Hashing                                   |       3 |
| Domain invariants (Document, chunk)       |       4 |

### Integration tests (14)

| Area                                                                          | # tests |
|-------------------------------------------------------------------------------|--------:|
| Query happy path with citations                                               |       1 |
| Query validation / 401 / permission leak / board access                       |       4 |
| Document ingest idempotency, list ACL filter, 403, validation                 |       4 |
| Chat turn (new session + follow-up) & feedback                                |       2 |
| Admin prompts (403, register-versioned)                                       |       2 |
| Evaluation harness (precision, recall, any-correct, paraphrase slice)         |       1 |

## Evaluation harness output

```
Seed: 0 document(s) ingested.
Running evaluation on 35 golden examples...

Retrieval metrics (K=5):
  Keyword   Recall@5=97.0%   MRR=0.885   nDCG@5=0.906
  Dense     Recall@5=87.9%   MRR=0.795   nDCG@5=0.815
  Hybrid    Recall@5=90.9%   MRR=0.842   nDCG@5=0.858

Citation precision      : 71.2%
Citation recall         : 81.8%
Any-correct-citation    : 81.8%
Refusal accuracy        : 94.3%
Total examples          : 35

Slice 'paraphrase' (8 examples):
  Keyword   Recall@5=87.5%   MRR=0.588   nDCG@5=0.660
  Dense     Recall@5=50.0%   MRR=0.354   nDCG@5=0.391
  Hybrid    Recall@5=75.0%   MRR=0.473   nDCG@5=0.540
```

(`Seed: 0` because the SQLite file survives from a prior run; ingestion is
idempotent by content hash. The initial-run seed count on a fresh DB is 22.)

See `docs/evaluation.md` for the diagnosis of the earlier 35.3% citation-precision
number and the fix that took it to 71.2%.

## Verified non-claims

- **No network access.** The default embedding + chat model implementations
  are `LocalDeterministicEmbeddingModel` and `TemplateChatModel`. The OpenAI
  and Azure OpenAI adapters compile and are unit-testable with a stubbed
  `HttpMessageHandler`, but they are gated behind `Ai:Provider=OpenAI` /
  `AzureOpenAI` and are never reached in tests or the default runtime.
- **No Docker required.** The Dockerfile and docker-compose.yml are shipped
  as UNVERIFIED reference artefacts (see the docker-compose header) — the
  entire test suite runs on Windows without a container runtime.
- **No Postgres/pgvector server.** The default `IVectorStore` is a
  SQLite-backed brute-force cosine + BM25 store, in-memory searched. The
  pgvector production path is documented in ADR 0003 but not implemented.
