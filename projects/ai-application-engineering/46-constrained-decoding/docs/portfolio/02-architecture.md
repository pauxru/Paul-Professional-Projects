# 02 — Architecture walkthrough

Five layers, each one independently testable, with the dependency arrow pointing
one way only.

```
JSON Schema text
      |
      v
  json.cpp ................ parser + serialiser + independent validity oracle
      |
      v
  regex.cpp ............... `pattern` -> NFA fragment
      |
      v
  schema.cpp .............. schema -> NFA fragment (composition, bounding, diagnostics)
      |
      v
  automaton.cpp ........... Thompson NFA -> subset construction -> minimisation
      |                     (+ product intersection, + DFA re-embedding)
      v
  mask.cpp ................ vocabulary trie -> per-state token mask -> cache
      |
      v
  capi.cpp ................ narrow C ABI, exception-proof
      |
      v
  schemafsm/ (Python) ..... ctypes bindings, bigram model, exact distribution analysis
```

## json.cpp — the parser that exists to disagree

There is a hand-written JSON parser in a project whose entire job is to avoid
parsing JSON. It is there to be an **independent oracle**.

The automaton and the parser are written from the same specification by the same
author, but through completely different mechanisms — one is a byte-level
finite automaton built by composition, the other is a recursive-descent parser.
When they disagree, one of them is wrong, and the disagreement is loud.

This paid for itself immediately. See `03-the-bug-the-oracle-found.md`.

## automaton.cpp — the only genuinely reusable piece

Thompson construction over **byte ranges**, not individual bytes. A UTF-8
continuation byte class is one edge `[0x80,0xBF]`, not 64 edges. This matters:
the string-body fragment for the four UTF-8 length classes is a few dozen states
instead of thousands, and epsilon-closure cost follows.

Subset construction is standard. Minimisation is Moore partition refinement,
with the addition that unproductive states — those from which no accepting state
is reachable — are dropped. That is not cosmetic: an unproductive state would
get its own entry in the mask cache and its own all-zero mask, and a decoder
that reached one would have no legal token and would wedge.

Two operations were added later, and both were forced by bugs rather than
planned:

- **`intersect`** — product construction, explored lazily from the start pair so
  only reachable products are materialised, then minimised. Needed because JSON
  Schema *conjoins* constraints while Thompson construction only offers union and
  concatenation.
- **`embed`** — splices a DFA back into a builder as a single-entry
  single-exit fragment, so the result of an intersection can be composed with the
  rest of the schema. Consecutive bytes sharing a target are coalesced into range
  edges; 256 singleton edges per state would have made the outer epsilon closure
  needlessly expensive.

## schema.cpp — where the honesty lives

This layer makes every decision that could quietly produce a wrong answer, so it
is where the diagnostics are. Three rules:

1. Anything not exactly representable is a **hard error** (recursive `$ref`).
2. Anything approximated must be approximated *downwards* — the automaton
   accepts a **subset** of the schema, never a superset (ADR 0002).
3. Every approximation emits a **diagnostic** the caller can read.

The reason for rule 2 is asymmetry of consequences. A subset approximation masks
away a legal document: the model is constrained more than intended, which is
visible and annoying. A superset approximation emits a document that the
automaton certified and the schema forbids: invisible, and it flows straight
into code that trusted the schema.

## mask.cpp — the hot path, and the honest benchmark

The vocabulary is a trie of `(byte, childIndex)` pairs, sorted, not a dense
256-way node — a dense node costs a kilobyte per node, which for 99,141 trie
nodes is 100 MB of mostly-empty pointers.

Mask computation walks the trie and the DFA together and prunes dead subtrees.
Masks are cached per DFA state, because the mask depends on the state and
nothing else.

The benchmark reports that **pruning loses on dense masks** — 0.7× versus brute
force on two of five schemas. That number is in the README rather than buried,
because a benchmark that only reports its wins is marketing.

## capi.cpp — the boundary

Primitives and caller-owned buffers only. Every entry point wrapped so no C++
exception can cross into ctypes. The vocabulary arrives as one blob plus an
array of lengths rather than 30,000 pointers.

Writing the Python side of this boundary is what surfaced the out-of-bounds read
in `applyMask` — the binding author has to ask "how long is this buffer?", and
the C++ author had never had to.

## schemafsm/ — the analysis layer

Deliberately not a thin wrapper. It contains the machinery that makes the
headline claim possible:

- `model.py` — SplitMix64 with a known-answer test, and a bigram model
- `distortion.py` — the product graph (DFA state × previous token), exhaustive
  enumeration with an explicit path-count guard, partition functions, the two
  distributions, the corrected decoder, and the divergences
- `decoding.py` — constrained, unconstrained and retry decoders plus the cost
  sweep
- `experiments.py` — regenerates every table in `docs/`

## What the layering bought

Each layer is testable against something other than itself: the automaton
against the oracle, the mask against brute force, the trie-pruned mask against
the linear scan, the corrected decoder against the closed-form conditional. Every
one of those cross-checks is a test, and two of them found real bugs.
