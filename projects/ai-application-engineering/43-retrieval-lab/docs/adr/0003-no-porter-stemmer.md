# ADR 0003: A minimal plural stripper, not Porter

## Status

Accepted.

## Context

Every classical retrieval baseline stems. Porter is the default, it is in every
IR toolkit, and using anything else invites the objection that the baselines
are weakened straw men.

The lab's purpose, though, is attribution: when a query fails, the report has
to say *which stage* lost the answer. That only works if each stage's
contribution is separable from the others.

Stemming is not separable in that way. Porter maps `plan` and `plane` to the
same term. In this corpus `plan` is the qualifier that distinguishes three
different correct values of the same fact — it is the single most
meaning-bearing token in the vocabulary. Porter also maps `retention` and
`retain` together, which is helpful, and `operate`/`operator` together, which
is not, and it does all of it with no per-decision record. A query that failed
because two distinct concepts were collapsed into one term is indistinguishable
in the metrics from a query that failed because the chunker severed a heading.

## Decision

Use a hand-written suffix stripper that handles only plural inflection:
`ies -> y`, `es` after a sibilant, and trailing `s` on tokens longer than four
characters that do not end in `ss`. Nothing else. No `-ing`, no `-ed`, no
`-ation`, no measure-of-consonant-sequences rule.

Apply it identically to documents and to queries, from one module, so the
chunker, the indexes and the metrics cannot drift apart about what a token is.

## Rationale

The rule is small enough to state in one sentence and to test exhaustively, and
its failure cases are enumerable: `test_text.py` pins `plans -> plan`,
`policies -> policy`, `access -> access` (the `ss` guard), `class -> class`,
`gas -> gas` (the length guard), `boxes -> box`, and — the one that matters —
`plane` and `plan` staying distinct.

That last case is the whole argument. Under Porter, section 6's finding that
the `implicit` query class is where every lexical retriever collapses would be
partly a stemmer artefact and there would be no way to tell how much. Under
this rule the vocabulary the retriever sees is one the reader can reconstruct.

The cost is real: the lexical baselines are weaker than they would be with full
morphological normalisation, because the corpus does contain `retention` in a
heading and `retained` in the sentence beneath it. Section 6 shows the effect
concentrated in the `paraphrase` class. This is disclosed rather than fixed,
because fixing it would purchase a higher absolute number at the price of the
attribution the lab exists to produce.

## Consequences

- Absolute nDCG here is lower than a Porter-stemmed baseline would report on
  the same corpus. Absolute numbers were already non-transferable (ADR 0001);
  this widens the gap and the README says so.
- The stop list is short for the same reason. A standard 400-word list deletes
  `for`, `within`, `before` and `after` — the exact tokens that distinguish
  "retained *for* 400 days" from "deleted *within* 400 days". The list here is
  32 words and is printed in the source.
- Comparisons *between* retrievers remain valid: every retriever sees the same
  analysed tokens, and the analyser is applied at exactly one place in the
  pipeline.

## Alternatives considered

**Porter / Snowball.** Rejected above. Would be the right call for a system
being shipped; it is the wrong call for an instrument.

**No stemming at all.** Considered. Rejected because `plans` in a query and
`plan` in a heading is a pure vocabulary accident with no diagnostic value —
it would inject noise into every class without illuminating anything. Plural
inflection is the one morphological process this corpus exercises constantly.

**Lemmatisation with a dictionary.** Rejected: adds a dependency and a data
file, and makes the analyser's behaviour a lookup rather than a rule the reader
can follow.
