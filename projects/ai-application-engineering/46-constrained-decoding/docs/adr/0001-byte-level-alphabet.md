# ADR 0001: the automaton's alphabet is bytes, not characters

**Status:** accepted

## Context

The automaton has to answer one question, millions of times: *given the current
state, which of the model's tokens may be emitted next?* A token is whatever
the tokenizer decided it was — in a BPE vocabulary that is an arbitrary byte
string, frequently one that straddles a structural boundary. `{"`, `":`, `",\n`
and `"}` are all single tokens in common vocabularies.

The obvious alphabet for a JSON automaton is Unicode code points, because that
is what JSON is defined over.

## Decision

The alphabet is **bytes**, 0–255.

## Consequences

A token is legal in state *s* exactly when running its bytes from *s* never
enters the dead state. That is a direct, exact computation with no re-derivation
of the tokenizer's segmentation and no assumption that tokens align to character
boundaries.

UTF-8 well-formedness is a regular language, so it is expressed *inside* the
automaton rather than assumed outside it. The string-body construction encodes
the four UTF-8 length classes with their exact continuation-byte ranges, which
means the automaton rejects overlong encodings and stray continuation bytes for
free.

It also means the surrogate-pair rule inside `\uXXXX` escapes had to be modelled
explicitly, because it too is regular: the first two hex digits determine
whether a low surrogate must follow. This was not obvious in advance — see
`docs/portfolio/03-the-bug-the-oracle-found.md`.

Length bounds are consequently measured in **bytes** when combined with a
`pattern`, and the compiler emits a diagnostic saying so rather than letting a
caller assume code points.

## Alternatives considered

**Code-point alphabet with a byte-level adapter.** Rejected: the adapter is
exactly the hard part, and getting it wrong produces a mask that is subtly
wrong rather than obviously wrong. A token that ends mid-character has no
code-point interpretation at all.

**Character alphabet plus post-hoc UTF-8 validation.** Rejected: post-hoc
validation cannot be applied to a *prefix*, and the mask is fundamentally a
question about prefixes.
