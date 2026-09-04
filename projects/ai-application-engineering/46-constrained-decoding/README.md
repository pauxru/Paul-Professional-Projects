# Constrained Decoding: a schema-to-automaton compiler with an honest account of what it costs

## The story

A team ships a feature that asks a language model to return JSON. It works in
the demo. In production, roughly one call in twenty comes back with a trailing
comma, a missing brace, or a plausible-looking field that the schema never
mentioned. The fix everyone reaches for first is to retry: parse the output,
and if it fails, ask again. It works, and the bill goes up, and the p99 latency
goes up, and nobody can say by how much because the retry rate depends on the
prompt.

The better fix is to make invalid output unrepresentable. Compile the schema
into a finite automaton, and at every decoding step mask out the tokens that
would take the automaton into a dead state. The model physically cannot emit a
trailing comma, because the token was removed from the distribution before
sampling.

That is the well-known part, and it is not why this project exists.

This project exists because **masking the tokens is not the same as sampling
from the model conditioned on validity**, and almost every deployment quietly
assumes it is. Both approaches produce only valid documents, so no amount of
testing, validating, or eyeballing will ever reveal the difference. It is
invisible by construction. Here it is measured exactly:

> On a three-way enum, greedy per-token masking produces a distribution over
> documents whose KL divergence from the true conditional is **1.42 nats**, with
> a total variation distance of **0.55**. In 4 of 15 measured configurations the
> *most likely valid document changes* depending on which method you use.

The correction is implemented too, and shown to recover the exact conditional
to within 1e-12 — which is precisely how you learn that the correction is
unusable in practice, because it needs the total probability mass of all valid
completions behind every candidate token, and no transformer can tell you that.

So the honest summary of constrained decoding is: *it guarantees syntactic
validity, and it silently biases your output distribution, and you should know
the size of the bias before you decide that is a good trade.*

## What is here

| Component | Language | What it does |
|---|---|---|
| `src/core/json.*` | C++20 | Dependency-free JSON parser, doubling as an independent validity oracle for the tests |
| `src/core/automaton.*` | C++20 | Thompson NFA over byte ranges, subset construction, Moore minimisation, product intersection |
| `src/core/regex.*` | C++20 | Regex subset for JSON Schema `pattern` |
| `src/core/schema.*` | C++20 | JSON Schema subset → DFA |
| `src/core/mask.*` | C++20 | Vocabulary trie, trie-pruned mask computation, per-state mask cache |
| `src/bindings/capi.*` | C ABI | Narrow, exception-proof boundary |
| `python/schemafsm/` | Python 3.12 | ctypes bindings, finite-state model, exact distribution analysis |
| `bench/` | C++20 | Compilation, masking, caching and deduplication benchmarks |

86 tests: 42 C++, 44 Python.

## Why this is hard

**A tokenizer does not emit characters.** A language model emits tokens, and a
token is an arbitrary byte string that may straddle any structural boundary —
`":` is one token in most vocabularies, and so is `{"`. An automaton over
characters cannot answer "is this token legal here" without re-deriving the
tokenizer's segmentation. So the automaton here is over **bytes**, and a token
is legal in state *s* exactly when running its bytes from *s* never enters the
dead state. UTF-8 well-formedness is itself a regular language, so nothing is
lost.

**Naively checking every token every step is quadratic-ish.** For each of ~30k
tokens you would run its bytes through the DFA at every decode step. The
implementation walks a trie of the vocabulary instead, sharing work across
common prefixes and pruning entire subtrees the moment the automaton dies.
Measured: **8.5×–17.8× faster when the allowed set is sparse, and 0.7× — i.e.
slower — when it is dense.** That negative result is reported in full in
`docs/benchmark-results.md`; the trie's pointer chasing loses to a tight linear
scan when almost nothing gets pruned.

**Not every schema is regular.** A schema with statically bounded nesting is
regular. A schema with a recursive `$ref` is context-free, and no DFA can
enforce it. This compiler *rejects* recursive `$ref` rather than silently
approximating it, because an automaton that quietly accepts a superset of the
schema is worse than no automaton — it produces confidently invalid output that
passes your validity check.

**JSON objects do not have ordered keys.** Allowing every legal key order is a
combinatorial blow-up: subsets × permutations. It is bounded here by an explicit
cap with a diagnostic when the cap is hit, and the cost is measured — key-order
freedom costs **1.0×–5.7× more DFA states**.

**Constraints conjoin, but Thompson construction only offers union and
concatenation.** `{"pattern": "[ab]*", "maxLength": 2}` is an intersection of
two languages. An earlier version of this compiler honoured `pattern` and
silently dropped the length bounds — the automaton accepted `"aaa"`, which the
schema rejects, and a constrained decoder would have emitted it with total
confidence. Fixed with a proper product construction (`intersect` in
`automaton.cpp`), with the unsatisfiable case reported as an error rather than
compiled into an automaton that accepts nothing and wedges the decoder.

## The results

All numbers are regenerated by `demo.ps1`. Nothing below is hand-typed.

### Masking is not conditioning

Exact, not sampled: the valid set is enumerated in full and both distributions
are computed in closed form.

| case | documents | KL (masked ‖ conditional) | total variation | most likely document |
|---|---|---|---|---|
| enum-3 | 3 | 1.4166 | 0.5512 | same |
| bool-object | 2 | 0.4428 | 0.1925 | same |
| two-key-object | 8 | 0.5564 | 0.3489 | **differs** |
| short-string | 7 | 0.4565 | 0.3663 | **differs** |
| small-array | 6 | 0.9829 | 0.5064 | **differs** |

The lookahead-reweighted decoder reproduces the exact conditional to within
1e-12 in every case, which is what proves the gap is caused by greedy local
renormalisation and not by a bug.

### Tokenisation ambiguity

A decoder samples token sequences; a user reads documents. The map is
many-to-one, and the probability of a document is a sum over its tokenisations
— a sum no left-to-right decoder ever computes.

| case | valid token sequences | distinct documents | max tokenisations of one document |
|---|---|---|---|
| enum-3 | 14 | 3 | 6 |
| bool-object | 23 | 2 | 14 |
| two-key-object | 64 | 8 | 8 |

### Retry versus constrain

| single-shot validity | mean retry attempts | retry tokens | constrained tokens | retry cost |
|---|---|---|---|---|
| 0.0% | 50.00 (gave up) | 337.6 | 5.8 | never terminates |
| 5.0% | 18.14 | 254.7 | 5.4 | 11 of 200 gave up |
| 13.5% | 5.82 | 101.5 | 5.5 | **18.5×** |

This is a decision rule, not a victory lap. Constrained decoding costs a fixed
mask per step; retrying costs an unbounded number of passes. Where the crossover
sits depends on the single-shot validity rate, which is a property of your model
and your prompt.

### Compilation and masking

| schema | NFA states | DFA states | minimised | reduction | compile |
|---|---|---|---|---|---|
| person | 28,746 | 6,914 | 1,481 | 78.6% | 1,411 ms |
| classification | 32,462 | 10,374 | 2,201 | 78.8% | 1,908 ms |

Mask deduplication is the strongest single optimisation: many DFA states permit
exactly the same token set, so the cache stores far fewer masks than states.

| schema | DFA states | distinct masks | dedup ratio | naive | deduped |
|---|---|---|---|---|---|
| person | 1,481 | 138 | **10.7×** | 5,473 KB | 516 KB |
| classification | 2,201 | 152 | **14.5×** | 8,133 KB | 570 KB |

## Honesty about the model

**No language model was used anywhere in this project.** The model is a
deterministic bigram over a small vocabulary, seeded by a hand-written
SplitMix64 with a known-answer test.

This is not a shortcut, it is the point. The headline claim is an *exact*
statement about a distribution, and you can only state it exactly if the
partition function over all valid completions is computable. A bigram model is
the largest family for which that is true. Every number here is therefore exact
rather than estimated — and every number here is about the *mechanics* of
constrained decoding, not about the behaviour of any particular LLM.

## Running it

```powershell
.\build.ps1     # MSVC + CMake + Ninja for the core, venv + pytest for Python
.\test.ps1      # 42 C++ tests, 44 Python tests
.\demo.ps1      # regenerates every table in docs/
```

Requires a C++20 compiler (MSVC via Visual Studio) and Python 3.12. The Python
package uses `ctypes`, so it needs no compiler of its own.

## Documentation

- [`docs/adr/`](docs/adr) — the five decisions that shaped this, with the
  alternatives that were rejected and why
- [`docs/known-limitations.md`](docs/known-limitations.md) — what this does not
  do, stated plainly
- [`docs/security-review.md`](docs/security-review.md) — threat model for a
  library that parses untrusted schemas and is called across a C ABI
- [`docs/benchmark-results.md`](docs/benchmark-results.md) — raw benchmark output
- [`docs/experiment-results.md`](docs/experiment-results.md) — raw experiment output
- [`docs/portfolio/`](docs/portfolio) — design walkthrough and engineering notes
