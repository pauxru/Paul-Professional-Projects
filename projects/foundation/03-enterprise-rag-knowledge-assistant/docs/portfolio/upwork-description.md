# Upwork / freelance description

## Short pitch (140 chars)

Enterprise RAG service in .NET 10 with permission-aware retrieval, citations,
grounding checks, prompt versioning and a real eval harness. Runs offline.

## Long description (Upwork body)

I build production-grade retrieval augmented generation (RAG) services — not
notebook demos. This project is a fully offline C# / ASP.NET Core service
that:

- ingests documents through a versioned pipeline with idempotent
  content-hash deduplication and two chunking strategies (fixed-size and
  sentence-aware);
- retrieves with three switchable modes — BM25 keyword (hand-built inverted
  index, no library), dense cosine over a deterministic embedding, and a
  hybrid Reciprocal-Rank-Fusion of both;
- filters retrieval by **ACL** at two independent layers so restricted
  content never reaches an unauthorised user's prompt (with a leak test
  that proves it);
- generates answers with inline `[n]` citations and a grounding /
  faithfulness check that downgrades unsupported answers to a polite
  refusal;
- versions its prompt templates by content hash and records the exact
  prompt version used on every answer;
- runs a real evaluation harness against a 35-question golden set (27
  direct + 8 paraphrase queries), reporting Recall@k, MRR, nDCG@k,
  citation precision, citation recall, any-correct-citation rate and
  refusal accuracy — numbers published in the repo, verified in tests;
- ships with JWT auth + policies, ProblemDetails errors, rate limiting,
  OpenTelemetry, correlation ids, a per-tenant budget guard with 429s, and
  70 automated tests (56 unit + 14 integration) that pass without any
  network or paid API key.

The default AI provider is a deterministic local implementation so the
entire pipeline is unit-testable and reproducible. Swapping to OpenAI or
Azure OpenAI is a configuration switch — the adapters are already
implemented.

## Deliverables (typical engagement)

- Codebase in your repo of choice (GitHub / Azure DevOps), reviewed against
  your team's coding standards.
- ADRs for every load-bearing decision (5 in this reference project).
- Security review with a STRIDE table and explicit non-claims.
- Evaluation harness wired to your golden set, with the initial baseline
  metrics.
- CI pipeline (GitHub Actions or Azure Pipelines) running build + test on
  every push.

## What I need from you

- A representative document corpus (10–100 docs) and a rough list of
  questions users should be able to ask.
- Your authentication story (I plug into JWT / OIDC / mTLS as required).
- Your production storage preference (SQLite for dev; pgvector, Azure AI
  Search, Weaviate, or similar for production).

I don't reinvent the wheel — I use ecosystem tools (`dotnet`, `EF Core`,
`Serilog`, `OpenTelemetry`) and integrate them the way a mature team would
review.
