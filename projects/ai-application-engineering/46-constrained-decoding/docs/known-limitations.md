# Known limitations

Stated plainly. Every item here is something this project does not do, or does
only partially. Nothing is hidden behind "future work".

## Schema coverage

- **Recursive `$ref` is rejected, not supported.** A schema whose type refers to
  itself describes a context-free language; a DFA cannot recognise it. The
  compiler fails with an error rather than approximating. This is the single
  biggest gap. Fixing it means a pushdown automaton, which invalidates the
  per-state mask cache (the mask would depend on stack depth), so it is a
  redesign rather than an addition. See ADR 0002.
- **`oneOf` is compiled as `anyOf`.** Branch disjointness is not verified, so a
  document matching two branches is accepted. A diagnostic is emitted.
- **Numeric `minimum` / `maximum` are not enforced.** Only the sign is. Digit-wise
  range automata are constructible and are simply not implemented; a diagnostic
  is emitted so the caller is never misled.
- **`multipleOf`, `uniqueItems`, `dependentRequired`, `if`/`then`/`else`,
  `patternProperties`, `propertyNames` and `not` are unsupported.** `not` in
  particular is regular (complementation is closed for DFAs) and would be
  reasonable to add.
- **`format` is ignored entirely.** A `"format": "email"` string is compiled as a
  plain string. It is not silently half-enforced; it is not enforced at all.
- **Unbounded strings are bounded silently but diagnosably.** A string with no
  `maxLength` is capped at a default and a diagnostic says so. This is a subset
  approximation — the automaton accepts fewer documents than the schema — which
  is the safe direction, but it does mean a legitimate long string will be
  masked away.

## Regex subset

- The `pattern` compiler supports character classes, alternation, `*`, `+`, `?`,
  bounded repetition, anchors and grouping. It does **not** support
  backreferences (not regular), lookahead/lookbehind, or non-greedy quantifiers
  (a DFA has no notion of greediness — it recognises a language, not a match).
- Patterns are restricted to a JSON-safe ASCII alphabet. A pattern containing a
  raw non-ASCII byte is rejected.
- When `pattern` is combined with `minLength`/`maxLength`, the bounds are applied
  as a byte-count intersection. For the ASCII alphabet the regex accepts, bytes
  and characters coincide — but the diagnostic says "bytes" because the guarantee
  is about bytes.

## Key ordering

- Key-order freedom is enumerated up to `maxObjectAlternatives` (default 720).
  Beyond that the compiler falls back to a fixed canonical order and says so.
  An object with more than about six optional properties will hit this.
- The efficient encoding — a seen-keys bitmask carried in the automaton state,
  2^n instead of ∑C(n,k)·k! — is not implemented. See ADR 0003.

## Performance

- **Trie-pruned mask computation is slower than brute force when the mask is
  dense** (measured 0.7× on two of five benchmark schemas). No adaptive
  selection is implemented. See ADR 0004.
- No SIMD anywhere. The brute-force path in particular would benefit.
- The mask cache is unbounded. For a schema with a very large minimised DFA and
  a large vocabulary, memory grows with the number of *distinct masks* — measured
  at 516 KB for a 1,481-state schema over a 30k vocabulary, so this is unlikely
  to matter, but there is no eviction policy.
- Compilation is single-threaded. Subset construction dominates and is the
  obvious parallelisation target.

## The distribution analysis

- **The model is a bigram, not a language model.** This is deliberate and
  explained in ADR 0005: the exactness of the result depends on the partition
  function being computable. The measured divergences characterise the *decoding
  mechanism*, not any particular LLM. Whether a transformer's distortion is
  larger or smaller than a bigram's is not answered here and is not answerable by
  this method.
- **Exact analysis only scales to enumerable cases.** The largest case analysed
  has 64 valid token sequences. `count_valid` refuses to enumerate above two
  million sequences, and the guard exists because a 40-byte string over a
  three-token vocabulary has a few thousand product-graph nodes and more than
  10^9 paths through them. There is no sampling-based estimator for larger cases.
- The lookahead correction is implemented for the bigram case only, and is
  demonstrated in order to show it is impractical — not offered as a feature.
- Tokenisation ambiguity is measured but not corrected. Marginalising over
  tokenisations during decoding is not implemented.

## Operational

- No streaming/incremental API: the caller drives the state machine one token at
  a time and owns the loop.
- No serialisation of a compiled automaton. Every process pays the compilation
  cost (measured at 1.4–1.9 s for the two largest benchmark schemas), which for a
  server would be a startup cost worth eliminating.
- Windows/MSVC is the only configuration actually built and tested here. The
  CMake and the code are portable — no Windows headers, no MSVC extensions, and
  the non-MSVC warning flags are wired up — but "should compile" is not
  "compiles", and it has not been verified on GCC or Clang.
