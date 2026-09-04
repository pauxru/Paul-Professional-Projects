# ADR 0005 — Grounding / faithfulness check on every answer

**Status:** Accepted &nbsp; **Date:** 2025 &nbsp; **Owner:** Retrieval / Trust

## Context

An LLM (or, in our case, an extractive template model) will happily produce a
fluent sentence that has no support in any retrieved chunk. In an enterprise
Q&A system this is unacceptable — hallucinated facts can be repeated as if
they were policy. The industry term for the guardrail is "groundedness" or
"faithfulness": does every sentence in the answer trace to a retrieved
source?

## Decision

Every answer passes through `LexicalGroundingChecker` before it is returned.

**Algorithm.**

1. Split the candidate answer into sentences (naive splitter — periods,
   question marks, exclamation marks, newlines; same splitter as the sentence
   chunker, for consistency).
2. For each sentence, compute a **support score** against each retrieved
   chunk: the fraction of non-stop tokens in the sentence that appear in the
   chunk, weighted by simple lexical overlap. Rejects citation markers
   `[n]` and other punctuation.
3. A sentence is *supported* if its best support score across chunks meets
   `MinSupportForSentence` (default 0.25). The chunk with the best support
   becomes the sentence's evidence and drives the citation set.
4. The answer's **support ratio** is `supported_sentences / total_sentences`.
   If it is below `MinSupportRatio` (default 0.4), the answer is downgraded
   to the refusal message with `RefusalReason = "insufficient-grounding"`.

**Why lexical, not semantic.**

The default embedding is deterministic but not sharp enough to declare
"this sentence is entailed by that paragraph". Lexical overlap is a
conservative, high-precision signal: if the answer's words appear together
in a source chunk, it is very likely a paraphrase of that chunk.

When a real LLM is plugged in (`Ai:Provider=OpenAI`), a natural upgrade is
to run an NLI-style entailment check as a second layer. Left as future
work.

## Consequences

**Positive.**

- Refusal is a first-class outcome, not an exception. `AnswerResult` has a
  `Refused` boolean, a `RefusalReason` string, and a `SupportRatio`.
- Citation building falls out of the same pass: only chunks that actually
  supported at least one sentence become citations, so the citation list
  reflects the evidence, not just the top-k.
- Testable: `GroundingCheckerTests` covers the boundary cases (no support,
  full support, mixed support, ignored citation markers).

**Negative.**

- Lexical overlap is defeated by paraphrase. A sentence that says "PTO caps
  at five days carry-over" when the source says "carry over up to five days"
  scores lower than it should. The support ratio threshold is deliberately
  lenient to compensate.
- The 25 % / 40 % thresholds are magic constants. They live in
  `RagAnsweringOptions` and can be tuned per environment (`appsettings.json`
  → `Rag:MinSupportForSentence` and `Rag:MinSupportRatio`).

## Alternatives considered

- **Trust the model, cite the top-k anyway.** Fastest, but produces
  ungrounded citations — the anti-pattern this ADR exists to prevent.
- **Full NLI model.** Requires shipping an entailment model, which conflicts
  with the "no network, no heavyweight local model" rule.
