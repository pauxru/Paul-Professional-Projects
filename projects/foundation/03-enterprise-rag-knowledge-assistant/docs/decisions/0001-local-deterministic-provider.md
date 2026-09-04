# ADR 0001 — Local deterministic AI provider is the default

**Status:** Accepted &nbsp; **Date:** 2025 &nbsp; **Owner:** Platform

## Context

We need to ship a portfolio-grade RAG service that anyone can build, test and
demo without a paid API key, network access, or an LLM runtime. At the same
time we must not become a toy: retrieval quality, refusal behaviour and prompt
versioning have to be verifiable with real assertions, not mocked away.

The obvious options were:

1. Mock every embedding / chat call in tests and require an API key otherwise.
2. Ship a heavyweight local LLM (llama.cpp, ONNX Runtime, etc.).
3. Ship a small, deterministic, in-process implementation of `IEmbeddingModel`
   and `IChatModel` that produces useful — not merely valid — output.

## Decision

We chose option 3 and made it the **default** provider.

- `LocalDeterministicEmbeddingModel` produces a 384-dim vector using
  token-hashed TF-IDF-style features (`FNV-1a` word bucketing with signed
  sublinear TF) mixed with character-trigram features (weighted 0.25), then L2
  normalised. Cosine similarity is meaningful — the ordering-property test
  asserts that a relevant sentence beats an unrelated sentence for a given
  query.
- `TemplateChatModel` is an **extractive** answer synthesiser. It reads the
  retrieved chunks (passed through a `<<CTX_JSON>>` marker on the system
  message), scores each sentence in the corpus against the user question,
  composes an answer with inline `[1][2]` citation markers, and refuses when
  no chunk clears a lexical support threshold.
- OpenAI and Azure OpenAI adapters (`OpenAiChatModel`,
  `OpenAiEmbeddingModel`, `AzureOpenAiEmbeddingModel`) are implemented against
  `HttpClient`, gated behind `Ai:Provider=OpenAI|AzureOpenAI` plus env-var
  credentials. They are never reached by tests or the default configuration.

## Consequences

**Positive.**

- `dotnet build && dotnet test` runs with zero network calls and zero secrets.
- Retrieval / grounding / refusal behaviour is deterministic, so tests can
  assert content and score ordering, not just success codes.
- Evaluation metrics reported in `docs/evaluation.md` are reproducible.

**Negative.**

- The extractive answer is not generative. It cannot paraphrase or reason
  across chunks. The real value proposition of the template model is the
  *shape* of the answer + citation pipeline, not the phrasing.
- The deterministic embedding is deliberately not competitive with a real
  encoder on paraphrase-heavy queries. This is documented in the eval report.
- Switching to a real provider is a config-only change (`Ai:Provider=OpenAI`
  + env vars), but requires a network egress and cost policy in production.

## Alternatives considered

- **Ollama / llama.cpp local model.** Adds a runtime dependency and slows CI
  by orders of magnitude.
- **Mock everything.** Loses the ability to make quality assertions in tests.
