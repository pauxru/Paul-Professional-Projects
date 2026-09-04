# Interview talking points

## Elevator pitch (30 seconds)

"I built a portfolio-grade enterprise RAG service in C#/ASP.NET Core. It has
permission-aware retrieval, inline citations, a grounding check that prevents
ungrounded answers, prompt versioning, and a real evaluation harness that
computes Recall/MRR/nDCG and citation precision/recall against a 35-question
golden set — 27 direct queries plus 8 deliberately paraphrased ones. It runs
entirely offline with a deterministic local embedding and chat model, so 70
automated tests pass without any paid API key — but the real OpenAI adapter
is behind a config switch."

## Why did you build the deterministic local provider?

Because a portfolio-grade project has to be reproducible. If you can only
demonstrate the pipeline by burning API credits, you can't run 70 tests in
CI. The local embedding is a hashed TF-IDF-ish + character-trigram feature
vector, L2-normalised. It passes an ordering-property test on a fixed
corpus — a relevant sentence has higher cosine to the query than an
unrelated one. The chat model is extractive, not generative, and refuses
below a grounding threshold. That gives me real determinism to assert on.

## Why hybrid retrieval when keyword-only still wins overall MRR?

Two answers, both honest:

1. **Overall keyword wins because the corpus is short and hand-authored.**
   That's documented in `docs/evaluation.md`. When I explicitly stress
   vocabulary mismatch — 8 paraphrase queries in the golden set — hybrid
   beats dense (Recall@5 75 % vs 50 %, MRR 0.473 vs 0.354). Even there,
   keyword still wins because the deterministic embedding isn't a real
   neural model — it can't bridge "annual leave" ↔ "paid time off".
2. **Hybrid is an insurance policy, not a demo trick.** In a production
   corpus users don't share the docs' vocabulary. Hybrid RRF degrades
   gracefully when either signal is weak; keyword-only fails hard on
   synonyms.

## Tell me about the citation-precision incident.

Early eval showed citation precision at 35.3 %. It looked like the pipeline
was hallucinating sources. I turned on a per-example diagnostic and found
two real bugs: (a) the answer composer was ranking sentences by
question-token overlap across ALL retrieved chunks, so a stray sentence in
an unrelated chunk could win a slot; (b) `AnsweringService` was throwing
away the composer's inline `[N]` markers and re-deriving citations from the
grounding checker's per-sentence best-match — which was picking
lexically-similar-but-wrong chunks. I fixed both, added citation recall and
"any-correct-citation" alongside precision, and pinned it with new unit and
integration tests. Precision went from 35.3 % to 71.2 % with recall at 81.8
%. I also added acceptable-alternates for two questions that genuinely span
two documents at Acme, and documented that in the report. The lesson: never
tune the golden set to make the number better — fix the code and, if the
metric is under-specified, add more metrics.

## How do you prevent the RAG service from leaking restricted docs?

Two layers, both enforced.

1. Every retriever call takes the `UserPrincipal`. The store filter drops
   any document whose ACL does not allow that user *before* scoring. That
   also keeps restricted content out of the BM25 term statistics for the
   query.
2. After retrieval, `AclPostFilter` re-checks the ACL for every returned
   chunk against a fresh document lookup. Unknown documents are dropped
   too — fail closed.

Then there's an integration test — `Query_RestrictedDoc_LeaksNothing` —
that asks a restricted question as an unauthorised user and asserts (a) no
citation carries the restricted document title, and (b) forbidden
substrings ("LTIP", "3.5 million") never appear in the answer. That's the
proof.

## What does your grounding check actually do?

Splits the answer into sentences with the same splitter the chunker uses
(consistency). For each sentence, computes lexical support against each
retrieved chunk — token overlap, minus stop-words, minus citation markers.
If a sentence's best support is below `MinSupportForSentence` (0.25), it's
counted as unsupported. If the ratio of supported sentences is below
`MinSupportRatio` (0.4), the whole answer is dropped and the assistant
refuses with `reason=insufficient-grounding`. Citations that come out are
only the chunks that actually supported a sentence — not the top-k blindly.

## How does prompt versioning work?

Prompts are content-hashed on registration. Registering the same
`(name, version)` with a different body returns 409. Registering a new
version deactivates all previous versions of the same name. Every answer
records the exact prompt id and `name@version` string it used, so I can
replay a query against the same prompt six months later.

## What did you *not* build?

- A full pgvector adapter — documented in ADR 0003, not implemented. On our
  brute-force cosine over 200 chunks it makes no measurable difference.
- Cross-tenant vector isolation. Today `Tenant` scopes the budget ledger
  but not the store. Roadmap.
- Ownership-based ACLs (per-user document ownership). We ship
  role/department/classification only.
- An entailment-model second layer for grounding. Lexical support is a
  precision-optimised first layer; adding NLI is future work when a real
  LLM is enabled.

## Anything you would change if you had another week?

- Add EF Core migrations. Today the schema is created via
  `EnsureCreatedAsync` for the dev experience.
- Cache the document-ACL lookup in `AclPostFilter` per request.
- Parameterise BM25 (k₁, b) via `RagOptions` so ops can tune without a
  rebuild.
- Extend the golden set with 10 paraphrased variants of the existing
  questions — that is where hybrid should finally overtake keyword.
