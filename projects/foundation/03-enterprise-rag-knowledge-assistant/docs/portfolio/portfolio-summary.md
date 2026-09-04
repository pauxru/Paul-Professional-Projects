# Portfolio summary — Enterprise RAG Knowledge Assistant

## One-liner

Production-grade retrieval-augmented Q&A service in C#/ASP.NET Core with
permission-aware retrieval, citations, grounding checks, prompt versioning
and a real evaluation harness — all runnable offline with zero paid API keys.

## Why it matters

Most "RAG demos" are notebooks that call an LLM and print citations. This
project is engineered as a service:

- **Provider abstraction.** `IEmbeddingModel` / `IChatModel` with a
  deterministic local default and real OpenAI / Azure OpenAI adapters
  behind a feature switch. Never accidentally hits a paid API in CI.
- **Hybrid retrieval.** Hand-built BM25 inverted index + cosine dense
  retrieval + Reciprocal Rank Fusion. All three modes are comparable
  through the same eval harness.
- **Permission-aware retrieval.** ACLs enforced at the store *and* as a
  post-filter. A leak test asserts that a restricted document never reaches
  an unauthorised user, either as a citation or as a substring in the
  answer.
- **Grounding / faithfulness check.** Every answer sentence must have
  lexical support in a cited chunk; otherwise the answer is downgraded to a
  refusal.
- **Prompt versioning.** Templates are hashed on registration; every answer
  records the exact prompt id + version used.
- **Real evaluation harness.** Golden dataset of 27 questions with expected
  documents; the harness reports Recall@k, MRR, nDCG@k, citation precision
  and refusal accuracy per retrieval mode. Numbers are in
  `docs/evaluation.md`, verified in tests.
- **Enterprise plumbing.** JWT + policies, ProblemDetails errors, rate
  limiting, OpenTelemetry traces/metrics, correlation ids, budget guard
  with per-tenant caps that return 429.

## Stack

`.NET 10` · `ASP.NET Core Minimal APIs` · `EF Core (SQLite)` ·
`OpenTelemetry` · `xUnit` · `Microsoft.AspNetCore.Mvc.Testing`

Runs on Windows/macOS/Linux with `dotnet build && dotnet test`. No Docker
required, no Python required.

## What to read first

1. `README.md` — architecture, endpoints, how to run.
2. `docs/evaluation.md` — real measured retrieval metrics with an honest
   interpretation.
3. `docs/decisions/` — five decision records covering the
   provider abstraction, hybrid retrieval, vector store choice, ACL layers,
   and the grounding check.
4. `docs/security/security-review.md` — STRIDE with explicit non-claims.
5. `tests/RagAssistant.IntegrationTests/Api/QueryEndpointsTests.cs` — the
   leak test that proves the permission-aware retrieval works end to end.
