# ADR-002: Use BM25 with k1=1.2 and b=0.75

## Context
Text relevance needs a transparent lexical baseline. BM25 balances term-frequency saturation, inverse-document-frequency discrimination, and field-length normalisation.

## Options
1. Raw term frequency.
2. TF-IDF without saturation.
3. BM25 with defaults.
4. Learned-to-rank only.

## Decision
Use BM25 with `k1=1.2` and `b=0.75`, configurable through typed options. Field boosts multiply the BM25 term contribution; function scoring applies after retrieval.

## Consequences
The scorer is easy to explain and is verified against a hand-computed two-document fixture. Default parameters are sensible but corpus-dependent, so configuration remains explicit.

## Risks
Commercial or support intent may need different boosts, synonyms, and relevance judgements. Function boosts can distort pure text relevance if made too large.

## Alternatives
Tune values from judged traffic, use per-field similarity in Elasticsearch, or add learned reranking after the current deterministic baseline is stable.
