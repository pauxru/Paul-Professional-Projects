# ADR-0004: A partial confusable table and one level of decoding

**Status:** accepted

## Context

The normaliser has to decide how far to go. Two questions have no natural
stopping point:

1. **Confusables.** Unicode's full confusables mapping is thousands of
   entries, and the security profile (UTS #39) is a specification in its own
   right.
2. **Nested encoding.** A base64 payload can contain a base64 payload. An
   attacker with a decoder that runs to fixpoint has a denial-of-service
   primitive; an attacker facing a decoder that stops has an evasion.

## Decision

Ship a **partial** confusable table covering the Latin/Cyrillic/Greek
homoglyphs that appear in practice, applied only to words that already mix
scripts. Decode **exactly one level** of base64, hex, and URL encoding.

Both limits are declared in the report, and the corpus contains attacks that
defeat both.

## Rationale

**The partial table is honest about being partial.** A complete table would
make the harness's confusable-detection rate a property of the table rather
than of the design, and would invite the reader to conclude the problem is
solved. It is not: the report's section 5 shows lexical detection rising from
3.6% to 78.6% with normalisation, and the residual is real.

**Word-scoping is a correctness requirement, not an optimisation.** This is
bug 1. Unscoped folding rewrote a benign Russian document into Latin gibberish
and flagged it with a top-weight structural signal — an outage with a
demographic attached, where every user writing in a non-Latin script is
blocked and every user writing English sees a working product. A word entirely
in one script is a word. A word mixing Cyrillic `о` into Latin `ignore` is an
attack, and *mixing* is the signature.

**One level of decoding is a stated bound, not an oversight.** Running to
fixpoint is unbounded work on attacker-controlled input. Stopping at one level
means `base64(base64(payload))` reaches the model undecoded — and the corpus
includes exactly that attack, so the cost appears in the numbers rather than
in a footnote.

## Consequences

- rot13 is not handled at all. It appears in `ENCODING_LEGIBILITY` with a low
  legibility score, because the *model* also struggles with it — and that
  score is declared by the corpus rather than inferred from this normaliser,
  which is bug 5 and ADR-0005's neighbour.
- The decoded payload is exposed on `Normalization.decoded` rather than
  spliced into `.text`. Substituting decoded content into the prompt would
  mean the defence hands the model a *cleaner* version of the attack.
