# 03 — Three bugs, and the tests that found them

None of these were found by reading the code. All three were found by a test
that checked the implementation against something built by a different method.
That is the transferable lesson: for a component whose output is hard to eyeball,
the highest-value test is a cross-check against an independent implementation.

---

## Bug 1 — the automaton accepted a lone surrogate

**Found by:** `every_accepted_string_is_valid_json`, which generates random
strings by walking the DFA and validates each one with the hand-written JSON
parser.

**The failure:**

```
{"tags":[],"age":504,"name":"B…\ude04"}
```

`\ude04` is a low surrogate with no preceding high surrogate. It cannot be
encoded in UTF-8 and strict JSON parsers reject it. The automaton emitted it
happily.

**Why:** the string-escape fragment was `\u` followed by four hex digits. Four
hex digits is the whole rule in the JSON grammar, and it is *not* the whole rule
in practice — a value in `D800–DBFF` must be followed by a second `\u` escape in
`DC00–DFFF`, and a value in `DC00–DFFF` must not appear alone.

**Why it mattered more than it looks:** this is exactly the superset
approximation from ADR 0002. The automaton certified a document as valid; a
downstream `json.loads` would have rejected it. The user's retry loop — the one
constrained decoding was supposed to eliminate — comes back, except now it
triggers on a case nobody can reproduce.

**The fix:** surrogate pairing is regular. The first two hex digits determine
which class you are in, so the escape fragment became an alternation of three
cases: a non-surrogate value, a high surrogate *immediately followed by* a
complete low-surrogate escape, and no bare low surrogate at all. Encoded
directly in the automaton, no post-hoc check.

**Regression test:** `schema_string_requires_surrogate_pairing`.

---

## Bug 2 — `pattern` silently discarded `maxLength`

**Found by:** writing the Python experiment cases. A schema of
`{"type":"string","maxLength":2,"pattern":"[ab]*"}` was expected to have a small
finite valid set. The enumeration ran until the stack overflowed.

**Why:** `compileString` checked for `pattern` first and returned immediately if
it found one. `minLength` and `maxLength` were never read. The automaton for
that schema accepted `"aaaaaaaaaa"`.

**Why it mattered:** the same superset failure as bug 1, but worse, because it is
not an exotic corner. Combining a `pattern` with a length bound is completely
ordinary — it is how you write "an identifier of at most 32 characters".

**The fix:** the two constraints are a language *intersection*, and Thompson
construction offers no intersection. So `intersect` (product construction) and
`embed` (DFA back into a fragment) were added to `automaton.cpp`, and
`compileString` now determinises the pattern, determinises a byte-counting
automaton for the bounds, intersects them, and splices the result back in.

The unsatisfiable case — `{"pattern":"ab","minLength":3}` — is detected and
reported as an error, rather than compiled into an automaton with no accepting
states that would wedge the decoder on its first token with no explanation.

**Regression tests:** `schema_intersects_pattern_with_length_bounds`,
`schema_intersects_pattern_with_minimum_length`,
`schema_rejects_unsatisfiable_pattern_length_combination`,
`intersect_is_language_intersection`, `embed_round_trips_a_dfa_through_the_builder`.

---

## Bug 3 — an out-of-bounds read in the logit masking

**Found by:** writing the ctypes bindings. The binding author has to decide how
many bytes to allocate for the mask buffer, and that forced the question the C++
author had never had to ask.

**Why:** `cdec_apply_mask` computed the mask length from the *logit* count:

```cpp
cdec::TokenMask mask(words, words + ((count + 63) / 64));
```

The logit vector is longer than the vocabulary whenever the model has special
ids — EOS, padding — which is always. If the vocabulary size is an exact
multiple of 64 the mask has no spare bits, so `(count + 63) / 64` is one word
larger than the buffer the caller allocated.

**Why it mattered:** an out-of-bounds read of caller-allocated memory, from a
function called once per generated token, whose triggering condition is
`vocab_size % 64 == 0` — so it works fine in every test until someone ships a
vocabulary of exactly 32,768 tokens.

**The fix:** the ABI carries an explicit `word_count`, and `applyMask`
bounds-checks the bit index against `mask.size() * 64`, treating anything past
the end as disallowed. The comment explains *why* rather than what, because the
condition is non-obvious.

**Regression test:** `apply_mask_tolerates_logits_longer_than_the_mask`, which
uses a vocabulary of exactly 64 tokens.

That test also failed on its first run — for a different reason. `applyMask`
only ever *suppresses* logits; it never restores them. Asserting EOS was allowed
after previously asserting it was not required refilling the vector. A test that
fails for the wrong reason is still a test doing its job.

---

## The fourth thing, which was not a bug

While writing the Python tests, a case with `maxLength: 40` over a three-token
vocabulary hung. The product graph is small — under 5,000 nodes, and acyclic —
so the existing node-count guard did nothing. The problem was the number of
*paths*: more than 10^9.

Path count is a linear-time dynamic program over a DAG. Enumeration is
exponential. So `count_valid` computes the count first and `enumerate_valid`
refuses above a limit, with a message naming the actual number.

Discovering the problem by running out of memory is not a diagnostic.

**Test:** `test_enumeration_refuses_exponentially_many_sequences`.
